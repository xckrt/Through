using System.Net;
using System.Text;
using Tropa.Core.Control;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>Общая база стратегий: подсказки меняют порядок подбора, отчёты уходят только с разрешения.</summary>
internal static class CommunityTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Общая база: подсказка провайдера пробуется первой; итог уходит, только если разрешено", async () =>
        {
            var engine = new FakeEngineHost((_, _) => false);
            await using var svc = FakeServer.SniFilter("svc.test", reset: true, () => engine.PresetFor("svc.test") is "a" or "b");
            var base_ = new FakeBase();
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-comm-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                await using var c = new TropaController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["svc"] = [new ProbeTarget { Name = "svc", Host = "svc.test", Port = svc.Port }] } },
                    Presets = new PresetCatalog { Presets = [P("a", 1), P("b", 2)] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts,
                    Community = new StrategyBase("https://base.test", base_),
                });
                await c.StartAsync();
                var s = await c.TuneAsync();
                Assert(s.Services.Single().PresetId == "b", $"по базе первой должна идти b, взята {s.Services.Single().PresetId}");
                await Task.Delay(300);
                Assert(base_.Reports.Count == 0, "без разрешения ничего не отправляется");

                await c.SetCommunityAsync(use: true, share: true);
                await c.TuneAsync();
                for (var i = 0; i < 20 && base_.Reports.Count == 0; i++) await Task.Delay(100);
                Assert(base_.Reports.Count == 1 && base_.Reports[0].Contains("\"preset\":\"b\"") && base_.Reports[0].Contains("\"ok\":true"), string.Join(" | ", base_.Reports));
                Assert(!base_.Reports[0].Contains("svc.test") && !base_.Reports[0].Contains("127.0.0.1"), "в отчёте не должно быть хостов и адресов");

                await c.SetCommunityAsync(use: false, share: false);
                var plain = await c.TuneAsync();
                Assert(plain.Services.Single().PresetId == "a", "без базы — обычный порядок по стоимости");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),

        ("Ранний сигнал: у провайдера массово сломалась наша стратегия — сторож переподбирает с первой проверки, с подсказкой", async () =>
        {
            var engine = new FakeEngineHost((_, _) => false);
            await using var svc = FakeServer.SniFilter("svc.test", reset: true, () => engine.PresetFor("svc.test") is "b");
            var base_ = new FakeBase { Alert = true };
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-alert-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                new TuningProfile { Services = new() { ["svc"] = new ServiceProfile { PresetId = "a", Complete = true } } }.Save(Path.Combine(dir, "profile.json"));
                File.WriteAllText(Path.Combine(dir, "settings.json"), """{"protect":true,"shareResults":true}""");
                await using var c = new TropaController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["svc"] = [new ProbeTarget { Name = "svc", Host = "svc.test", Port = svc.Port }] } },
                    Presets = new PresetCatalog { Presets = [P("a", 1), P("c", 2), P("b", 3)] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts,
                    Community = new StrategyBase("https://base.test", base_),
                    WatchdogStrikes = 2,
                });
                await c.StartAsync();
                await c.WatchdogTickAsync();
                Assert(c.Status().Profile["svc"] == "b", $"с одной проверки и сразу «b»: {string.Join(",", c.Status().Profile)}");
                Assert(engine.Started.Count(x => x.Contains("c")) == 0, "подсказка «b» должна идти раньше более дешёвой «c»");
                for (var i = 0; i < 20 && !base_.Reports.Any(r => r.Contains("\"status\":[{")); i++) await Task.Delay(100);
                Assert(base_.Reports.Any(r => r.Contains("\"blocked\":true") && r.Contains("\"protected\":true")), "статус поломки ушёл (делиться включено)");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),

        ("community.json: пустой или не https адрес — базы нет", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"tropa-community-{Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, "{ // пусто\n \"url\": \"\" }");
                Assert(StrategyBase.FromConfig(path) is null, "пустой адрес");
                File.WriteAllText(path, "{ \"url\": \"http://plain.example\" }");
                Assert(StrategyBase.FromConfig(path) is null, "без https не ходим");
                File.WriteAllText(path, "{ \"url\": \"https://tropa.example.workers.dev/\" }");
                Assert(StrategyBase.FromConfig(path)?.BaseUrl == "https://tropa.example.workers.dev", "https — да");
            }
            finally
            {
                File.Delete(path);
            }
            return Task.CompletedTask;
        }),

        ("Свежий список: берём только валидные домены и только известные сервисы, порты и winws не трогаем", async () =>
        {
            var handler = new CatalogHandler("""{"version":3,"services":{"youtube":["new-cdn.googlevideo.com","BAD DOMAIN","--dpi-desync=evil","dup.com","dup.com"],"games":["x.com"]}}""");
            var overlay = await new StrategyBase("https://base.test", handler).CatalogAsync(CancellationToken.None);
            Assert(overlay is not null && overlay.Version == 3, $"версия: {overlay?.Version}");
            var yt = overlay!.Services["youtube"];
            Assert(yt.Count == 2 && yt.Contains("new-cdn.googlevideo.com") && yt.Contains("dup.com"), $"валидные домены: {string.Join(",", yt)}");
            Assert(!yt.Any(d => d.Contains(' ') || d.Contains("--")), "мусор и аргументы winws отфильтрованы");

            var cat = new TargetCatalog { Services = new() { ["youtube"] = [] }, Domains = new() { ["youtube"] = ["youtube.com"] } };
            var merged = cat.WithExtraDomains(overlay.Services);
            Assert(merged.Domains["youtube"].Contains("new-cdn.googlevideo.com") && merged.Domains["youtube"].Contains("youtube.com"), "домены добавлены к youtube");
            Assert(!merged.Domains.ContainsKey("games"), "неизвестный сервис не добавляется");
            Assert(!merged.Ports.ContainsKey("youtube"), "порты с сервера не трогаются");
        }),
    ];

    private sealed class CatalogHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static StrategyPreset P(string id, int cost) => new() { Id = id, Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"], Cost = cost };

    private static readonly ProbeOptions Opts = new()
    {
        Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        AllowPrivateAddresses = true,
        ValidateCertificate = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        TlsTimeout = TimeSpan.FromSeconds(1),
        ReadIdleTimeout = TimeSpan.FromSeconds(1),
        NeutralSni = null,
    };

    private sealed class FakeBase : HttpMessageHandler
    {
        public List<string> Reports { get; } = [];
        public bool Alert;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (Reports) Reports.Add(body);
                return Json("{\"stored\":1}");
            }
            if (request.RequestUri!.AbsolutePath == "/v1/alerts")
                return Json(Alert
                    ? """{"asn":1,"org":"Тест","alerts":[{"service":"svc","preset":"a","users":5,"fix":"b"}]}"""
                    : """{"alerts":[]}""");
            return Json("{\"asn\":1,\"org\":\"Тест-Телеком\",\"services\":{\"svc\":[{\"preset\":\"b\",\"ok\":9,\"fail\":1,\"users\":4,\"score\":0.83},{\"preset\":\"a\",\"ok\":0,\"fail\":5,\"users\":3,\"score\":0.14}]}}");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
