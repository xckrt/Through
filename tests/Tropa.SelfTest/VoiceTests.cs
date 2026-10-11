using System.Net;
using System.Net.Sockets;
using Tropa.Core.Control;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>Голос Discord: UDP IP Discovery и подбор варианта голосового профиля.</summary>
internal static class VoiceTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Голос: сервер ответил на IP Discovery → OK; молчит → UdpBlocked на этапе передачи", async () =>
        {
            using var voice = new FakeVoice(() => true);
            var ok = await new ServiceProber(Opts(voice.Port)).ProbeAsync(Target);
            Assert(ok.Verdict == Verdict.Ok && ok.Detail!.Contains("ответил"), $"{ok.Verdict} {ok.Detail}");

            voice.Answer = () => false;
            var bad = await new ServiceProber(Opts(voice.Port)).ProbeAsync(Target);
            Assert(bad is { Verdict: Verdict.Blocked, Failure: Failure.UdpBlocked, FailedAt: ProbeStage.Transfer }, $"{bad.Verdict} {bad.Failure} {bad.FailedAt}");
        }),

        ("Подбор голоса: основной вариант молчит — перебирает запасные, запоминает рабочий, сторож делает это сам", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            using var voice = new FakeVoice(() => engine.Active.Contains("v-ipfrag"));
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-voice-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                new TuningProfile { Services = new() { ["discord"] = new ServiceProfile { PresetId = "a", Complete = true } } }
                    .Save(Path.Combine(dir, "profile.json"));
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"protect\":true}");
                await using var c = new TropaController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["discord"] = [Target] }, Domains = new() { ["discord"] = ["discord.test"] } },
                    Presets = new PresetCatalog
                    {
                        Presets = [new StrategyPreset { Id = "a", Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"] }],
                        Companions =
                        [
                            new CompanionProfile { Id = "v-main", Service = "discord", Group = "voice", Args = ["--wf-udp=1"] },
                            new CompanionProfile { Id = "v-any", Service = "discord", Group = "voice", Alternative = true, Args = ["--wf-udp=1"] },
                            new CompanionProfile { Id = "v-ipfrag", Service = "discord", Group = "voice", Alternative = true, Args = ["--wf-udp=1"] },
                        ],
                    },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts(voice.Port),
                    WatchdogStrikes = 2,
                });
                await c.StartAsync();
                Assert(engine.Active.Contains("v-main") && !engine.Active.Contains("v-any"), $"по умолчанию основной: {string.Join(",", engine.Active)}");

                await c.WatchdogTickAsync();
                Assert(engine.Active.Contains("v-main"), "одна неудача — ещё не повод");
                await c.WatchdogTickAsync();
                Assert(engine.Active.Contains("v-ipfrag") && !engine.Active.Contains("v-main"), $"сторож подобрал голос: {string.Join(",", engine.Active)}");
                Assert(TuningProfile.Load(Path.Combine(dir, "profile.json")).Companions["voice"] == "v-ipfrag", "выбор сохранён");
                Assert(c.Status().Targets.Single(t => t.Voice).IsOk, "голос проходит");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),

        ("Игровой фильтр: компаньон включается настройкой и уходит при выключении", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            using var voice = new FakeVoice(() => true);
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-game-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            try
            {
                new TuningProfile { Services = new() { ["discord"] = new ServiceProfile { PresetId = "a", Complete = true } } }
                    .Save(Path.Combine(dir, "profile.json"));
                File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"protect\":true}");
                await using var c = new TropaController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["discord"] = [Target] }, Domains = new() { ["discord"] = ["discord.test"] } },
                    Presets = new PresetCatalog
                    {
                        Presets = [new StrategyPreset { Id = "a", Treats = [Failure.TlsReset], Args = ["--hostlist={hostlist}"] }],
                        Companions = [new CompanionProfile { Id = "game-filter", Service = "games", GameFilter = true, Args = ["--wf-udp=1024-65535"] }],
                    },
                    Engine = _ => Task.FromResult(new EngineContext(engine, "test")),
                    DataDir = dir,
                    Probe = Opts(voice.Port),
                });
                await c.StartAsync();
                Assert(!engine.Active.Contains("game-filter"), $"по умолчанию выключен: {string.Join(",", engine.Active)}");

                await c.SetGameFilterAsync(true);
                Assert(engine.Active.Contains("game-filter") && engine.Active.Contains("a"),
                    $"включили — должен работать вместе со стратегией: {string.Join(",", engine.Active)}");
                Assert(c.Status().GameFilter, "в статусе отмечен включённым");

                await c.SetGameFilterAsync(false);
                Assert(!engine.Active.Contains("game-filter"), $"выключили — должен уйти: {string.Join(",", engine.Active)}");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),
    ];

    private static readonly ProbeTarget Target = new() { Name = "discord voice", Host = "voice.test", Voice = true, Optional = true, Role = "голос" };

    private static ProbeOptions Opts(int port) => new()
    {
        VoiceAddresses = ["127.0.0.1"],
        VoicePorts = [port],
        VoiceTimeout = TimeSpan.FromMilliseconds(400),
        Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        AllowPrivateAddresses = true,
        ConnectTimeout = TimeSpan.FromMilliseconds(300),
    };

    /// <summary>Голосовой сервер на loopback: отвечает на IP Discovery, когда Answer() == true.</summary>
    private sealed class FakeVoice : IDisposable
    {
        private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        public Func<bool> Answer;
        public int Port => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        public FakeVoice(Func<bool> answer)
        {
            Answer = answer;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await _udp.ReceiveAsync(_cts.Token);
                        if (!Answer() || r.Buffer.Length < 74) continue;
                        var reply = (byte[])r.Buffer.Clone();
                        reply[1] = 2;
                        await _udp.SendAsync(reply, r.RemoteEndPoint, _cts.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udp.Dispose();
        }
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
