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
    ];

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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                lock (Reports) Reports.Add(body);
                return Json("{\"stored\":1}");
            }
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
