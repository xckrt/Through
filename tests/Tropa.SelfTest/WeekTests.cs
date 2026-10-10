using Tropa.Core.Control;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>Недельная сводка: часы под защитой, сон не считается, через 7 дней — новая неделя, сводка переживает перезапуск.</summary>
internal static class WeekTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Неделя: часы под защитой копятся, сон компьютера не считается, через 7 дней — новая неделя", async () =>
        {
            var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
            var engine = new FakeEngineHost((_, _) => true);
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-week-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                new TuningProfile { Services = new() { ["svc"] = new ServiceProfile { PresetId = "a", Complete = true } } }.Save(Path.Combine(dir, "profile.json"));
                File.WriteAllText(Path.Combine(dir, "settings.json"), """{"protect":true}""");
                ControllerOptions Opts() => new()
                {
                    Targets = new TargetCatalog { Services = new() { ["svc"] = [] }, Domains = new() { ["svc"] = ["svc.test"] } },
                    Presets = new PresetCatalog { Presets = [new StrategyPreset { Id = "a", Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"] }] },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Clock = () => now,
                };
                await using (var c = new TropaController(Opts()))
                {
                    await c.StartAsync();
                    c.SampleWeek();
                    for (var i = 0; i < 6; i++) { now = now.AddMinutes(10); c.SampleWeek(); }
                    Assert(Math.Abs(c.Status().Week!.Current.ProtectedHours - 1) < 0.01, $"час под защитой: {c.Status().Week!.Current.ProtectedHours}");

                    now = now.AddHours(8); // ночь: компьютер спал
                    c.SampleWeek();
                    Assert(Math.Abs(c.Status().Week!.Current.ProtectedHours - 1) < 0.01, "сон не считается");

                    now = now.AddDays(7);
                    c.SampleWeek();
                    var w = c.Status().Week!;
                    Assert(w.Previous is { ProtectedHours: > 0.9 } && w.Current.ProtectedHours == 0, $"новая неделя: {w}");
                }
                await using (var c = new TropaController(Opts()))
                {
                    await c.StartAsync();
                    Assert(c.Status().Week!.Previous is { ProtectedHours: > 0.9 }, "прошлая неделя переживает перезапуск");
                }
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
