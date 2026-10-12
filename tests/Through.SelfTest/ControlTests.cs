using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using Through.Core.Control;
using Through.Core.Probing;
using Through.Core.Tuning;

namespace Through.SelfTest;

/// <summary>Логика службы (ThroughController) и канал (ControlServer/ControlClient) поверх фейкового движка.</summary>
internal static class ControlTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Compose: общий --wf-tcp с объединёнными портами, профили через --new", () =>
        {
            var a = new EngineLaunch(P("a", ["--wf-tcp={ports}", "--filter-tcp={ports}", "--hostlist={hostlist}", "--dpi-desync=fake"]), ["x"], [80, 443]);
            var b = new EngineLaunch(P("b", ["--wf-tcp={ports}", "--hostlist={hostlist}", "--dpi-desync=split2"]), ["y"], [443]);
            var args = EngineLaunch.Compose([a, b], ["/h/a.txt", "/h/b.txt"], "/e");
            var line = string.Join(" ", args);
            Assert(line == "--wf-tcp=80,443 --filter-tcp=80,443 --hostlist=/h/a.txt --dpi-desync=fake --new --hostlist=/h/b.txt --dpi-desync=split2", line);
            return Task.CompletedTask;
        }),

        ("Compose: диапазоны UDP-портов не теряются", () =>
        {
            var voice = new EngineLaunch(P("v", ["--wf-udp=19294-19344,50000-50100", "--filter-l7=discord,stun"]), ["x"], [443]);
            var quic = new EngineLaunch(P("q", ["--wf-udp=443", "--filter-udp=443"]), ["y"], [443]);
            var line = string.Join(" ", EngineLaunch.Compose([voice, quic], ["/a", "/b"], "/e"));
            Assert(line.StartsWith("--wf-udp=443,19294-19344,50000-50100 "), line);
            return Task.CompletedTask;
        }),

        ("BypassScope: в обход идут все домены сервиса и его доп. порты, не только цели проверки", () =>
        {
            var c = new TargetCatalog
            {
                Services = new() { ["discord"] = [new ProbeTarget { Name = "api", Host = "Discord.com" }] },
                Domains = new() { ["discord"] = ["discord.com", "discordapp.net"] },
                Ports = new() { ["discord"] = [8443, 2053] },
            };
            var (hosts, ports) = c.BypassScope("discord");
            Assert(hosts.SequenceEqual(["discord.com", "discordapp.net"]), string.Join(",", hosts));
            Assert(ports.SequenceEqual([443, 2053, 8443]), string.Join(",", ports));
            return Task.CompletedTask;
        }),

        ("Служба: сопутствующий профиль (QUIC, голос) включается вместе со стратегией; не принят — защита без него", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            Assert(w.Engine.Active.Contains("alpha-extra"), $"active={string.Join(",", w.Engine.Active)}");

            await w.Controller.SetProtectionAsync(false);
            w.Engine.FailToStart.Add("alpha-extra");
            var on = await w.Controller.SetProtectionAsync(true);
            Assert(on.Protecting && !w.Engine.Active.Contains("alpha-extra") && w.Engine.Active.Contains("a"),
                $"active={string.Join(",", w.Engine.Active)}");
            Assert(on.Message?.Contains("alpha-extra") == true, on.Message ?? "");
        })),

        ("Служба: подбор двух сервисов → у каждого своя стратегия, защита включена одним winws", () => With(async w =>
        {
            var summary = await w.Controller.TuneAsync();
            Assert(summary.Services.All(s => s.Outcome == TuneOutcome.Fixed), Show(summary));
            var status = w.Controller.Status();
            Assert(status.Profile["alpha"] == "a" && status.Profile["beta"] == "b", string.Join(",", status.Profile));
            Assert(status.Protecting && w.Engine.Active.Where(x => x != "alpha-extra").Order().SequenceEqual(["a", "b"]), $"active={string.Join(",", w.Engine.Active)}");
            Assert(status.Targets.Where(t => !t.IsControl).All(t => t.IsOk), "после подбора проверка под защитой должна быть зелёной");
            Assert(status.Services.Single(s => s.Key == "beta").ActivePreset == "b", "в отчёте должен быть активный пресет");
            Assert(File.ReadAllText(Path.Combine(w.Dir, "settings.json")).Contains("true"), "желание защиты должно сохраниться");
        })),

        ("Служба: выключить и включить защиту; без профиля включить нельзя", () => With(async w =>
        {
            var err = await Throws(() => w.Controller.SetProtectionAsync(true));
            Assert(err.Contains("сначала запустите подбор"), err);

            await w.Controller.TuneAsync();
            var off = await w.Controller.SetProtectionAsync(false);
            Assert(!off.Protecting && !w.Engine.Running && !off.ProtectionWanted, "защита не выключилась");
            var on = await w.Controller.SetProtectionAsync(true);
            Assert(on.Protecting && w.Engine.Running, "защита не включилась");
        })),

        ("Служба: после перезапуска защита поднимается сама", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            await w.Controller.DisposeAsync();
            Assert(!w.Engine.Running, "при остановке службы winws должен выключаться");

            await using var again = w.NewController();
            await again.StartAsync();
            Assert(again.Status().Protecting && w.Engine.Active.Where(x => x != "alpha-extra").Order().SequenceEqual(["a", "b"]),
                $"после перезапуска active={string.Join(",", w.Engine.Active)}");
        })),

        ("Сторож: упавший winws перезапускается", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            var starts = w.Engine.Started.Count;
            w.Engine.Crash();
            await w.Controller.WatchdogTickAsync();
            Assert(w.Engine.Started.Count == starts + 1 && w.Engine.Running, $"starts {starts} → {w.Engine.Started.Count}");
            Assert(w.Controller.Status().Message?.Contains("перезапущен") == true, w.Controller.Status().Message ?? "");
        })),

        ("Сторож: стратегия перестала работать → после двух проверок переподбор только этого сервиса", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            w.AlphaPresetWorks = false; // провайдер «научился» обходить стратегию a
            await w.Controller.WatchdogTickAsync();
            Assert(w.Controller.Status().Profile["alpha"] == "a", "одна неудача не должна запускать переподбор");

            var before = w.Engine.Started.Count;
            await w.Controller.WatchdogTickAsync();
            var status = w.Controller.Status();
            Assert(status.Profile["alpha"] == "b", $"alpha должна переехать на b, профиль: {string.Join(",", status.Profile)}");
            Assert(status.Profile["beta"] == "b", "beta не трогали");
            Assert(status.Protecting && status.Targets.Where(t => !t.IsControl).All(t => t.IsOk), "после переподбора всё зелёное");
            Assert(w.Engine.Started.Skip(before).All(s => s.Split('+').Contains("b")),
                $"beta должна работать всё время переподбора alpha: {string.Join(" ", w.Engine.Started.Skip(before))}");
        })),

        ("Служба: подбор без результата не оставляет без старой защиты", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            w.NothingWorks = true;
            var s = await w.Controller.TuneAsync(["alpha"]);
            Assert(s.Services.Single().Outcome == TuneOutcome.NoFix, Show(s));
            Assert(w.Controller.Status().Protecting && w.Engine.Active.Where(x => x != "alpha-extra").Order().SequenceEqual(["a", "b"]),
                $"старая защита должна вернуться, active={string.Join(",", w.Engine.Active)}");
        })),

        ("Служба: пока идёт подбор, вторая операция получает отказ", () => With(async w =>
        {
            var tune = w.Controller.TuneAsync();
            await Task.Delay(100);
            var err = await Throws(() => w.Controller.CheckAsync());
            Assert(err.Contains("идёт подбор"), err);
            w.EngineGate.SetResult();
            await tune;
        }, gated: true)),

        ("Канал: status, tune с событиями попыток, protect, неизвестный метод, мусор", () => With(async w =>
        {
            var pipeName = $"through-test-{Guid.NewGuid():N}";
            using var cts = new CancellationTokenSource();
            var server = new ControlServer(w.Controller, pipeName).RunAsync(cts.Token);

            // клиент-хулиган шлёт не JSON: служба должна жить дальше
            await using (var raw = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await raw.ConnectAsync(2000);
                await raw.WriteAsync(Encoding.UTF8.GetBytes("{{{ not json\n"));
                await raw.FlushAsync();
            }

            await using var client = await ControlClient.TryConnectAsync(pipeName, TimeSpan.FromSeconds(2))
                ?? throw new Exception("клиент не подключился");
            var attempts = new List<string>();
            client.Event += (evt, data) =>
            {
                if (evt == "attempt") lock (attempts) attempts.Add(ControlProtocol.FromData<AttemptReport>(data).PresetId);
            };

            var status = await client.CallAsync<StatusReport>("status");
            Assert(status.State == ControllerState.Idle && !status.Protecting, "начальное состояние");

            var summary = await client.CallAsync<TuneSummary>("tune", new { services = new[] { "alpha" } });
            Assert(summary.Services.Single().PresetId == "a", Show(summary));
            lock (attempts) Assert(attempts.SequenceEqual(["a"]), $"события попыток: {string.Join(",", attempts)}");

            var off = await client.CallAsync<StatusReport>("protect", new { on = false });
            Assert(!off.Protecting && !w.Engine.Running, "protect off по каналу");

            var err = await Throws(() => client.CallAsync<StatusReport>("rm -rf"));
            Assert(err.Contains("неизвестный метод"), err);

            cts.Cancel();
            await server;
        })),

        ("Служба: своё приложение — добавить по каналу, подобрать, защитить, удалить", () => With(async w =>
        {
            await using var gamma = FakeServer.SniFilter("gamma.test", reset: true, () => w.Engine.PresetFor("gamma.test") == "b");
            var pipeName = $"through-test-{Guid.NewGuid():N}";
            using var cts = new CancellationTokenSource();
            var server = new ControlServer(w.Controller, pipeName).RunAsync(cts.Token);
            await using var client = (await ControlClient.TryConnectAsync(pipeName, TimeSpan.FromSeconds(2)))!;

            var err = await Throws(() => client.CallAsync<AppDefinition[]>("apps.add",
                new AppDefinition { Key = "alpha", Title = "Подмена", Domains = ["x.com"] }));
            Assert(err.Contains("встроенных"), err);

            var apps = await client.CallAsync<AppDefinition[]>("apps.add", new AppDefinition
            {
                Key = "gamma", Title = "Гамма", Domains = ["gamma.test"], CheckHosts = [$"gamma.test:{gamma.Port}"], UdpPorts = ["7777"],
            });
            Assert(apps.Single().Key == "gamma", "приложение не сохранилось");
            Assert(File.Exists(Path.Combine(w.Dir, "apps.json")), "apps.json не записан");

            var s = await client.CallAsync<TuneSummary>("tune", new { services = new[] { "gamma" } });
            Assert(s.Services.Single().PresetId == "b", Show(s));
            Assert(w.Engine.Active.Contains("b") && w.Engine.Active.Contains("gamma-udp"), $"active={string.Join(",", w.Engine.Active)}");
            var status = await client.CallAsync<StatusReport>("status");
            Assert(status.Services.Single(x => x.Key == "gamma").Title == "Гамма", "у своего приложения должно быть название");

            await client.CallAsync<AppDefinition[]>("apps.remove", new { key = "gamma" });
            status = await client.CallAsync<StatusReport>("status");
            Assert(!status.Profile.ContainsKey("gamma") && status.Services.All(x => x.Key != "gamma")
                   && status.Targets.All(t => t.Service != "gamma"), "приложение не удалилось целиком");
            Assert(!w.Engine.Active.Contains("gamma-udp"), $"игровой профиль остался: {string.Join(",", w.Engine.Active)}");

            cts.Cancel();
            await server;
        })),

        ("Обновление zapret: новая версия подхватывает защиту и запоминается", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            w.Engine2 = new FakeEngineHost((_, _) => false);
            w.Update = (current, _) => Task.FromResult<EngineContext?>(new EngineContext(w.Engine2, "v2"));

            var r = await w.Controller.UpdateEngineAsync();
            Assert(r.Updated && r.Version == "v2", r.Message);
            Assert(!w.Engine.Running && w.Engine2.Running, "защита должна переехать на новую версию");
            var status = await w.Controller.CheckAsync();
            Assert(status.EngineVersion == "v2" && status.Targets.Where(t => !t.IsControl).All(t => t.IsOk), "под новой версией всё должно работать");
            Assert(w.Accepted.SequenceEqual(["v2"]), "принятая версия должна запомниться");
        })),

        ("Обновление zapret: новая версия не стартовала → откат на прежнюю", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            w.Engine2 = new FakeEngineHost((_, _) => false) { FailToStart = { "a", "b" } };
            w.Update = (_, _) => Task.FromResult<EngineContext?>(new EngineContext(w.Engine2, "v2"));

            var r = await w.Controller.UpdateEngineAsync();
            Assert(!r.Updated && r.Message.Contains("не запустился"), r.Message);
            Assert(w.Engine.Running && !w.Engine2.Running && w.Controller.Status().EngineVersion == "test", "должна работать прежняя версия");
            Assert(w.Accepted.Count == 0, "неудачная версия не должна запоминаться");
        })),

        ("Обновление zapret: новая версия запустилась, но сломала то, что работало → откат и больше сама не ставится", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            w.Engine2 = new FakeEngineHost((_, _) => false);
            w.Engine2Breaks = true;
            w.Update = (_, _) => Task.FromResult<EngineContext?>(new EngineContext(w.Engine2, "v2"));

            var r = await w.Controller.UpdateEngineAsync();
            Assert(!r.Updated && r.Message.Contains("сломал") && r.Message.Contains("alpha"), r.Message);
            Assert(w.Engine.Running && !w.Engine2.Running && w.Controller.Status().EngineVersion == "test", "должна вернуться прежняя версия");
            Assert(w.Accepted.Count == 0, "сломавшая версия не запоминается как принятая");
            Assert(w.Controller.Status().Targets.Where(t => !t.IsControl).All(t => t.IsOk), "после отката всё снова работает");

            // автообновление ту же версию больше не трогает: она записана как отвергнутая
            Assert(File.ReadAllText(Path.Combine(w.Dir, "settings.json")).Contains("""
                "rejectedEngine": "v2"
                """), "версия должна запомниться как сломавшая");
        })),

        ("Обновление zapret: новее нет → сообщение; автообновление выключено → не проверяет", () => With(async w =>
        {
            await w.Controller.TuneAsync();
            var calls = 0;
            w.Update = (_, _) => { calls++; return Task.FromResult<EngineContext?>(null); };
            var r = await w.Controller.UpdateEngineAsync();
            Assert(!r.Updated && r.Message.Contains("последняя"), r.Message);

            await w.Controller.SetAutoUpdateAsync(false);
            await using var again = w.NewController();
            await again.StartAsync();
            await again.CheckAsync(); // движок поднят защитой при старте
            await again.MaybeAutoUpdateAsync();
            Assert(calls == 1, $"с выключенным автообновлением проверок быть не должно, было {calls}");
        })),

        ("Составной подбор: ни одна стратегия не берёт сервис целиком → своя для упрямого хоста", async () =>
        {
            // как dl.discordapp.net: основной хост лечит только «a», упрямый — только «b»
            var engine = new FakeEngineHost((_, _) => false);
            await using var main = FakeServer.SniFilter("main.test", reset: true, () => engine.PresetFor("main.test") == "a");
            await using var stubborn = FakeServer.SniFilter("stubborn.test", reset: true, () => engine.PresetFor("stubborn.test") == "b");
            var dir = Path.Combine(Path.GetTempPath(), $"through-svc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                await using var c = new ThroughController(new ControllerOptions
                {
                    Targets = new TargetCatalog
                    {
                        Services = new()
                        {
                            ["svc"] =
                            [
                                new ProbeTarget { Name = "main", Host = "main.test", Port = main.Port },
                                new ProbeTarget { Name = "stubborn", Host = "stubborn.test", Port = stubborn.Port },
                            ],
                        },
                    },
                    Presets = new PresetCatalog { Presets = [P("a", ["--hostlist={hostlist}"], 1), P("b", ["--hostlist={hostlist}"], 2)] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts,
                });
                await c.StartAsync();
                var s = await c.TuneAsync();
                Assert(s.Services.Single().Outcome == TuneOutcome.Fixed, Show(s));

                var status = c.Status();
                var report = status.Services.Single();
                Assert(report.ActivePreset == "a" && report.Overrides.GetValueOrDefault("stubborn.test") == "b",
                    $"основная={report.ActivePreset}, уточнения={string.Join(",", report.Overrides)}");
                Assert(engine.PresetFor("stubborn.test") == "b" && engine.PresetFor("main.test") == "a",
                    "уточнение должно стоять в winws раньше основной стратегии");
                Assert(status.Targets.All(t => t.IsOk), "под защитой со связкой всё должно работать");
                Assert(File.ReadAllText(Path.Combine(dir, "profile.json")).Contains("stubborn.test"), "уточнение должно сохраниться в профиль");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }),

        ("Второстепенная проверка не чинится → сервис «починен», сторож не рвёт обход переподбором", async () =>
        {
            // как dl.discordapp.net: клиент работает, а хост обновлений через несколько минут отваливается
            var engine = new FakeEngineHost((_, _) => false);
            await using var main = FakeServer.SniFilter("main.test", reset: true, () => engine.PresetFor("main.test") == "a");
            await using var updates = FakeServer.SniFilter("updates.test", reset: true, () => false);
            var dir = Path.Combine(Path.GetTempPath(), $"through-svc-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                await using var c = new ThroughController(new ControllerOptions
                {
                    Targets = new TargetCatalog
                    {
                        Services = new()
                        {
                            ["svc"] =
                            [
                                new ProbeTarget { Name = "main", Host = "main.test", Port = main.Port },
                                new ProbeTarget { Name = "updates", Host = "updates.test", Port = updates.Port, Optional = true, Role = "обновления" },
                            ],
                        },
                    },
                    Presets = new PresetCatalog { Presets = [P("a", ["--hostlist={hostlist}"], 1), P("b", ["--hostlist={hostlist}"], 2)] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts,
                    WatchdogStrikes = 2,
                });
                await c.StartAsync();
                var s = await c.TuneAsync();
                Assert(s.Services.Single().Outcome == TuneOutcome.Fixed && s.Services.Single().PresetId == "a", Show(s));

                var starts = engine.Started.Count;
                await c.WatchdogTickAsync();
                await c.WatchdogTickAsync();
                await c.WatchdogTickAsync();
                Assert(engine.Started.Count == starts, $"сторож перезапускал движок: {string.Join(" ", engine.Started.Skip(starts))}");
                var updatesReport = c.Status().Targets.Single(t => t.Name == "updates");
                Assert(!updatesReport.IsOk && updatesReport.Optional && updatesReport.Role == "обновления", "второстепенная должна остаться видна в отчёте");
            }
            finally { Directory.Delete(dir, recursive: true); }
        }),

        ("Канал: службы нет → клиент возвращает null, а не висит", async () =>
        {
            var c = await ControlClient.TryConnectAsync($"through-none-{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(300));
            Assert(c is null, "к несуществующей службе подключились");
        }),
    ];

    // ---------- обвязка

    private sealed class World : IAsyncDisposable
    {
        private readonly List<FakeServer> _servers = [];

        public required string Dir { get; init; }
        public required FakeEngineHost Engine { get; init; }
        public required TargetCatalog Targets { get; init; }
        public ThroughController Controller { get; set; } = null!;
        public TaskCompletionSource EngineGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Gated { get; init; }
        public volatile bool AlphaPresetWorks = true;

        /// <summary>Вторая «версия zapret» для тестов обновления.</summary>
        public FakeEngineHost? Engine2 { get; set; }
        public Func<string, CancellationToken, Task<EngineContext?>>? Update { get; set; }
        public List<string> Accepted { get; } = [];
        public volatile bool NothingWorks;
        public volatile bool Engine2Breaks;

        public void Own(FakeServer s) => _servers.Add(s);

        public ThroughController NewController() => new(new ControllerOptions
        {
            Targets = Targets,
            Presets = new PresetCatalog
            {
                Presets = [P("a", ["--hostlist={hostlist}"], 1), P("b", ["--hostlist={hostlist}"], 2)],
                Companions = [new CompanionProfile { Id = "alpha-extra", Service = "alpha", Args = ["--wf-udp=443"] }],
            },
            Engine = async ct =>
            {
                if (Gated) await EngineGate.Task.WaitAsync(ct);
                return new EngineContext(Engine, "test");
            },
            DataDir = Dir,
            Probe = Opts,
            WatchdogStrikes = 2,
            EngineUpdate = (v, ct) => Update is { } u ? u(v, ct) : Task.FromResult<EngineContext?>(null),
            EngineAccepted = c => Accepted.Add(c.Version),
        });

        public async ValueTask DisposeAsync()
        {
            await Controller.DisposeAsync();
            foreach (var s in _servers) await s.DisposeAsync();
        }
    }

    private static readonly ProbeOptions Opts = new()
    {
        Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        AllowPrivateAddresses = true,
        ValidateCertificate = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        TlsTimeout = TimeSpan.FromSeconds(1),
        ReadIdleTimeout = TimeSpan.FromSeconds(1),
    };

    /// <summary>
    /// Два сервиса за SNI-фильтром с RST. alpha чинит пресет a (пока AlphaPresetWorks) и пресет b,
    /// beta чинит только b. Так подбор выбирает a для alpha и b для beta.
    /// </summary>
    private static async Task With(Func<World, Task> body, bool gated = false)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"through-svc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var engine = new FakeEngineHost((_, _) => false);
        World? w = null;
        bool Uses(string host, string id) =>
            (engine.PresetFor(host) == id || (w!.Engine2?.PresetFor(host) == id && !w.Engine2Breaks)) && !w!.NothingWorks;

        var alpha = FakeServer.SniFilter("alpha.test", reset: true,
            () => (Uses("alpha.test", "a") && w!.AlphaPresetWorks) || Uses("alpha.test", "b"));
        var beta = FakeServer.SniFilter("beta.test", reset: true, () => Uses("beta.test", "b"));
        w = new World
        {
            Dir = dir,
            Engine = engine,
            Gated = gated,
            Targets = new TargetCatalog
            {
                Services = new()
                {
                    ["alpha"] = [new ProbeTarget { Name = "alpha", Host = "alpha.test", Port = alpha.Port }],
                    ["beta"] = [new ProbeTarget { Name = "beta", Host = "beta.test", Port = beta.Port }],
                },
            },
        };
        w.Own(alpha);
        w.Own(beta);
        w.Controller = w.NewController();
        try
        {
            await w.Controller.StartAsync();
            await body(w);
        }
        finally
        {
            await w.DisposeAsync();
            Directory.Delete(dir, recursive: true);
        }
    }

    private static StrategyPreset P(string id, string[] args, int cost = 1) =>
        new() { Id = id, Cost = cost, Treats = [Failure.TlsReset, Failure.TlsTimeout], Args = args };

    private static string Show(TuneSummary s) =>
        string.Join("; ", s.Services.Select(x => $"{x.Service}={x.Outcome}/{x.PresetId}"));

    private static async Task<string> Throws(Func<Task> body)
    {
        try { await body(); }
        catch (InvalidOperationException ex) { return ex.Message; }
        throw new Exception("ожидали ошибку, а всё прошло");
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
