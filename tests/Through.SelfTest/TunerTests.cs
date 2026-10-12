using System.Net;
using System.Net.Sockets;
using Through.Core.Probing;
using Through.Core.Tuning;

namespace Through.SelfTest;

/// <summary>Подборщик против FakeServer.SniFilter и фейкового движка, который «чинит» его.</summary>
internal static class TunerTests
{
    private const string Host = "example.test";

    public static IEnumerable<(string, Func<Task>)> All(ProbeOptions opts) =>
    [
        ("Tuner: только пресеты под диагноз, дешёвый рабочий, досрочный выход", async () =>
        {
            var engine = new FakeEngineHost((id, _) => id == "b");
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var report = await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets());

            var s = report.Services.Single();
            Assert(s.Outcome == TuneOutcome.Fixed, $"outcome={s.Outcome}");
            Assert(s.Best?.Preset.Id == "b", $"best={s.Best?.Preset.Id}");
            // t лечит другой диагноз, c дороже рабочего b
            Assert(engine.Started.SequenceEqual(["a", "b"]), $"started={string.Join(",", engine.Started)}");
            Assert(engine.LastLaunch!.Hosts.SequenceEqual([Host]) && engine.LastLaunch.Ports.SequenceEqual([srv.Port]),
                "движок должен быть сужен до хостов и портов сервиса");
            Assert(s.Plan.Diagnosis.Single().Failure == Failure.TlsReset, "диагноз ставится без обхода");
        }),

        ("Tuner: проигравший кандидат не гоняется лишний раз, победитель проверяется полностью", async () =>
        {
            var maxN = new Dictionary<string, int>();
            var engine = new FakeEngineHost((id, n) => { lock (maxN) maxN[id] = Math.Max(maxN.GetValueOrDefault(id), n); return id == "b"; });
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Best?.Preset.Id == "b", $"best={s.Best?.Preset.Id}");
            // «a» проигрывает на первом же прогоне → второй пропускаем; «b» (победитель) проходит оба
            Assert(maxN.GetValueOrDefault("a") < maxN.GetValueOrDefault("b"),
                $"проигравший должен гоняться меньше победителя: a={maxN.GetValueOrDefault("a")} b={maxN.GetValueOrDefault("b")}");
        }),

        ("Tuner: пресет, чинящий один прогон из двух, не выбирается", async () =>
        {
            var engine = new FakeEngineHost((id, n) => id == "a" ? n == 1 : id == "b");
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Best?.Preset.Id == "b", $"best={s.Best?.Preset.Id}");
            var a = s.Attempts.Single(x => x.Preset.Id == "a");
            Assert(!a.Score.IsPerfect, $"флейковый пресет засчитан: {a.Score}");
        }),

        ("Tuner: ничего не помогло → NoFix, все кандидаты в отчёте", async () =>
        {
            var engine = new FakeEngineHost((_, _) => false);
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Outcome == TuneOutcome.NoFix && s.Best is null, $"outcome={s.Outcome} best={s.Best?.Preset.Id}");
            Assert(s.Attempts.Select(x => x.Preset.Id).SequenceEqual(["a", "b", "c"]),
                $"attempts={string.Join(",", s.Attempts.Select(x => x.Preset.Id))}");
            Assert(engine.Stopped == 3 && !engine.Running, $"stopped={engine.Stopped}");
        }),

        ("Tuner: сервис работает без обхода → перебора нет", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            await using var srv = FakeServer.Https((st, ct) => FakeServer.WriteAsync(st, "HTTP/1.1 204 No Content\r\n\r\n", ct));
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Outcome == TuneOutcome.AlreadyOk, $"outcome={s.Outcome}");
            Assert(engine.Started.Count == 0, "движок не должен запускаться");
        }),

        ("Tuner: контрольные лежат → нет сети, перебора нет", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var report = await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port, controlPort: FreePort()), Presets());

            Assert(report.NetworkDown && report.Services.Count == 0, $"networkDown={report.NetworkDown}");
            Assert(engine.Started.Count == 0, "движок не должен запускаться");
        }),

        ("Tuner: TLS глушат на адресе → NotTreatable, перебора нет", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true);
            await using var srv = FakeServer.ResetAfterClientHello();
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Outcome == TuneOutcome.NotTreatable, $"outcome={s.Outcome}");
            Assert(engine.Started.Count == 0, "движок не должен запускаться");
        }),

        ("Tuner: движок не стартовал → попытка с ошибкой, перебор идёт дальше", async () =>
        {
            var engine = new FakeEngineHost((_, _) => true) { FailToStart = { "a" } };
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var s = (await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets())).Services.Single();

            Assert(s.Attempts[0].Error is not null, "ошибка запуска должна попасть в отчёт");
            Assert(s.Best?.Preset.Id == "b", $"best={s.Best?.Preset.Id}");
            Assert(engine.Stopped == 1 && !engine.Running, $"stopped={engine.Stopped}");
        }),

        ("Tuner: отмена посреди попытки → движок остановлен", async () =>
        {
            using var cts = new CancellationTokenSource();
            var engine = new FakeEngineHost((_, _) => { cts.Cancel(); return false; });
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            try
            {
                await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets(), ct: cts.Token);
                throw new Exception("ожидали OperationCanceledException");
            }
            catch (OperationCanceledException) { }
            Assert(engine.Started.Count == 1 && engine.Stopped == 1 && !engine.Running,
                $"started={engine.Started.Count} stopped={engine.Stopped}");
        }),

        ("Tuner: профиль сохраняется и читается обратно", async () =>
        {
            var engine = new FakeEngineHost((id, _) => id == "b");
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var report = await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), Presets());

            var path = Path.Combine(Path.GetTempPath(), $"through-profile-{Guid.NewGuid():N}.json");
            try
            {
                var old = new TuningProfile { Services = { ["other"] = new ServiceProfile { PresetId = "x" } } };
                old.Apply(report, DateTimeOffset.UnixEpoch).Save(path);
                var loaded = TuningProfile.Load(path);
                var svc = loaded.Services["svc"];
                Assert(svc.PresetId == "b" && svc.Complete && svc.Diagnosis.SequenceEqual([Failure.TlsReset]),
                    $"preset={svc.PresetId} complete={svc.Complete} diag={string.Join(",", svc.Diagnosis)}");
                Assert(loaded.Services["other"].PresetId == "x", "чужие записи профиля не трогаются");
                Assert(TuningProfile.Load(path + ".missing").Services.Count == 0, "нет файла → пустой профиль");
            }
            finally { File.Delete(path); }
        }),

        ("Tuner: пресет для другого сервиса не пробуется", async () =>
        {
            var engine = new FakeEngineHost((id, _) => id == "b");
            await using var srv = FakeServer.SniFilter(Host, reset: true, engine.AllowConnection);
            var presets = Presets() with
            {
                Presets = [.. Presets().Presets.Select(p => p.Id == "a" ? p with { Services = ["youtube"] } : p)],
            };
            await new Tuner(engine, opts).TuneAsync(Catalog(srv.Port), presets);
            Assert(engine.Started.SequenceEqual(["b"]), $"started={string.Join(",", engine.Started)}");
        }),

        ("presets.json: файлы только из официального релиза или закреплённых extras", () =>
        {
            // содержимое files/fake/ в bol-van/zapret v72.13 (то, что ставит Fetcher), без файлов Flowseal
            string[] official =
            [
                "stun.bin", "tls_clienthello_www_google_com.bin", "tls_clienthello_vk_com.bin", "tls_clienthello_vk_com_kyber.bin",
                "tls_clienthello_iana_org.bin", "tls_clienthello_gosuslugi_ru.bin", "tls_clienthello_sberbank_ru.bin",
                "tls_clienthello_rutracker_org_kyber.bin", "quic_initial_www_google_com.bin", "quic_initial_vk_com.bin",
            ];
            var catalog = PresetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "presets.json"));
            var extras = Through.Core.Engine.EngineManifest.Load(Path.Combine(AppContext.BaseDirectory, "engine.json")).Extras.Select(e => e.Name).ToList();
            var args = catalog.Presets.SelectMany(p => p.Args).Concat(catalog.Companions.SelectMany(c => c.Args)).ToList();
            foreach (var a in args.Where(a => a.Contains("{engineDir}/files/")))
            {
                var rest = a[(a.IndexOf("{engineDir}/files/", StringComparison.Ordinal) + "{engineDir}/files/".Length)..];
                var ok = rest.StartsWith("fake/") ? official.Contains(rest["fake/".Length..])
                       : rest.StartsWith("extra/") && extras.Contains(rest["extra/".Length..]);
                Assert(ok, $"{rest}: нет ни в официальном релизе, ни в extras engine.json ({a})");
            }
            Assert(catalog.Presets.Any(p => p.AppliesTo("youtube") && p.Services is not null), "нет стратегий под Google");
            return Task.CompletedTask;
        }),

        ("targets.json: обход шире проверки (иначе приложение Discord висит на «Starting…»)", () =>
        {
            var t = TargetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "targets.json"));
            var (yt, _) = t.BypassScope("youtube");
            var (dc, dcPorts) = t.BypassScope("discord");
            Assert(yt.Contains("googlevideo.com") && yt.Contains("youtube.com"), string.Join(",", yt));
            Assert(dc.Contains("discordapp.net") && dc.Contains("discord.gg") && dc.Contains("discord.media"), string.Join(",", dc));
            var companions = PresetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "presets.json")).Companions;
            Assert(companions.Any(c => c.Service == "discord" && c.Args.Any(a => a.Contains("8443"))),
                "медиа Discord на нестандартных портах должно идти отдельным профилем");
            return Task.CompletedTask;
        }),

        ("presets.json: читается, подстановки раскрываются", () =>
        {
            var catalog = PresetCatalog.Load(Path.Combine(AppContext.BaseDirectory, "presets.json"));
            Assert(catalog.Presets.Count > 0, "пустой каталог");
            foreach (var p in catalog.Presets)
                Assert(p.Args.Any(a => a.Contains("{hostlist}")), $"{p.Id}: без {{hostlist}} пресет ломал бы всю сеть");
            Assert(catalog.Treating(new HashSet<Failure> { Failure.TlsTimeout }).Count > 0, "нет пресетов от TlsTimeout");

            var args = new EngineLaunch(catalog.Presets[0], [Host], [80, 443]).RenderArgs("C:/t/hosts.txt", "C:/zapret");
            Assert(args.Contains("--wf-tcp=80,443") && args.Contains("--hostlist=C:/t/hosts.txt") && !args.Any(a => a.Contains('{')),
                string.Join(" ", args));
            return Task.CompletedTask;
        }),
    ];

    private static TargetCatalog Catalog(int port, int? controlPort = null)
    {
        var services = new Dictionary<string, List<ProbeTarget>>
        {
            ["svc"] = [new ProbeTarget { Name = "svc", Host = Host, Port = port }],
        };
        if (controlPort is { } cp)
            services["control"] = [new ProbeTarget { Name = "ctl", Host = "control.test", Port = cp, IsControl = true }];
        return new TargetCatalog { Services = services };
    }

    private static PresetCatalog Presets() => new()
    {
        Presets =
        [
            P("c", 3, Failure.TlsReset),
            P("t", 0, Failure.TransferStalled),
            P("a", 1, Failure.TlsReset, Failure.TlsTimeout),
            P("b", 2, Failure.TlsReset),
        ],
    };

    private static StrategyPreset P(string id, int cost, params Failure[] treats) =>
        new() { Id = id, Cost = cost, Treats = treats, Args = [$"--preset={id}", "--hostlist={hostlist}"] };

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
