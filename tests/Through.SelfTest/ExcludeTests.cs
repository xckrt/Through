using Through.Core.Control;
using Through.Core.Probing;
using Through.Core.Tuning;

namespace Through.SelfTest;

/// <summary>«Не трогать»: исключённые сайты не обходятся ни одним профилем winws.</summary>
internal static class ExcludeTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Не трогать: --hostlist-exclude-domains в каждом профиле, поддомены выпадают из hostlist", () =>
        {
            var p = new StrategyPreset { Id = "a", Treats = [Failure.TlsReset], Args = ["--wf-tcp=443", "--hostlist={hostlist}", "--dpi-desync=fake"] };
            var a = new EngineLaunch(p, ["youtube.com", "bank.example", "lk.bank.example"], [443]) { Exclude = ["bank.example"] };
            var b = new EngineLaunch(p with { Id = "b" }, ["x.test"], [443]) { Exclude = ["bank.example"] };
            Assert(a.EffectiveHosts.SequenceEqual(["youtube.com"]), string.Join(",", a.EffectiveHosts));
            var line = string.Join(" ", EngineLaunch.Compose([a, b], ["/a", "/b"], "/e"));
            Assert(line.Split("--new").All(part => part.Contains("--hostlist-exclude-domains=bank.example")), line);
            return Task.CompletedTask;
        }),

        ("Не трогать: служба принимает адрес, сводит к домену и перезапускает winws без него", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            var dir = Path.Combine(Path.GetTempPath(), $"through-excl-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                new TuningProfile { Services = new() { ["svc"] = new ServiceProfile { PresetId = "a", Complete = true } } }.Save(Path.Combine(dir, "profile.json"));
                File.WriteAllText(Path.Combine(dir, "settings.json"), """{"protect":true}""");
                await using var c = new ThroughController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["svc"] = [] }, Domains = new() { ["svc"] = ["svc.test", "pay.bank.example"] } },
                    Presets = new PresetCatalog { Presets = [new StrategyPreset { Id = "a", Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"] }] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                });
                await c.StartAsync();
                Assert(engine.PresetFor("pay.bank.example") == "a", "до исключения банк обходится");
                var s = await c.ExcludeAsync("https://online.bank.example/login", add: true);
                Assert(s.Excluded.SequenceEqual(["bank.example"]) && engine.PresetFor("pay.bank.example") is null && engine.PresetFor("svc.test") == "a",
                    $"исключён домен целиком, остальное обходится: {string.Join(",", s.Excluded)}");
                await c.ExcludeAsync("bank.example", add: false);
                Assert(engine.PresetFor("pay.bank.example") == "a", "вернули — снова обходится");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),
    ];

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
