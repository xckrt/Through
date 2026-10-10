using System.Net;
using System.Net.Sockets;
using Tropa.Core.Probing;
using Tropa.SelfTest;

// Без зависимостей: запускается `dotnet run --project tests/Tropa.SelfTest`.
// Каждый тест поднимает локальный сервер, изображающий конкретный способ блокировки,
// и проверяет, что пробник распознал его правильно.

if (args is [EngineTests.FakeWinwsFlag, var mode, ..])
    return await EngineTests.FakeWinwsAsync(mode, args[2..]);

var opts = new ProbeOptions
{
    Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
    AllowPrivateAddresses = true,
    ValidateCertificate = false,
    // Windows на RST повторяет SYN, и отказ на закрытый порт приходит только через ~2 с
    ConnectTimeout = TimeSpan.FromSeconds(5),
    TlsTimeout = TimeSpan.FromSeconds(1),
    ReadIdleTimeout = TimeSpan.FromSeconds(1),
};
var prober = new ServiceProber(opts);
ProbeTarget T(int port, int minBytes = 0, double? minKbps = null, bool control = false) =>
    new() { Name = "test", Host = "example.test", Port = port, MinBytes = minBytes, MinKbps = minKbps, IsControl = control };

var tests = new List<(string, Func<Task>)>
{
    ("RST после ClientHello → TlsReset", async () =>
    {
        await using var srv = FakeServer.ResetAfterClientHello();
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsReset, ProbeStage.Tls);
        Assert(r.SniFiltered == false, $"RST на любой SNI, SniFiltered={r.SniFiltered}");
    }),

    ("тишина после ClientHello → TlsTimeout", async () =>
    {
        await using var srv = FakeServer.SilentAfterClientHello();
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsTimeout, ProbeStage.Tls);
    }),

    ("дроп по SNI → TlsTimeout, SniFiltered=true", async () =>
    {
        await using var srv = FakeServer.SniFilter("example.test", reset: false);
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsTimeout, ProbeStage.Tls);
        Assert(r.SniFiltered == true, $"SniFiltered={r.SniFiltered} ({r.Detail})");
        Assert(r.Detail!.Contains("фильтр по SNI"), r.Detail);
    }),

    ("RST по SNI → TlsReset, SniFiltered=true", async () =>
    {
        await using var srv = FakeServer.SniFilter("example.test", reset: true);
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsReset, ProbeStage.Tls);
        Assert(r.SniFiltered == true, $"SniFiltered={r.SniFiltered} ({r.Detail})");
    }),

    ("глухо на любой SNI → SniFiltered=false", async () =>
    {
        await using var srv = FakeServer.SilentAfterClientHello();
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsTimeout, ProbeStage.Tls);
        Assert(r.SniFiltered == false, $"SniFiltered={r.SniFiltered} ({r.Detail})");
        Assert(r.Detail!.Contains("весь TLS"), r.Detail);
    }),

    ("NeutralSni = null → проверки нет", async () =>
    {
        await using var srv = FakeServer.SniFilter("example.test", reset: true);
        var r = await new ServiceProber(opts with { NeutralSni = null }).ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.TlsReset, ProbeStage.Tls);
        Assert(r.SniFiltered is null, $"SniFiltered={r.SniFiltered}");
    }),

    ("нейтральный SNI тоже фильтруется → SniFiltered=false", async () =>
    {
        await using var srv = FakeServer.SniFilter("example.test", reset: true);
        var r = await new ServiceProber(opts with { NeutralSni = "neutral.example.test" }).ProbeAsync(T(srv.Port));
        Assert(r.SniFiltered == false, $"SniFiltered={r.SniFiltered} ({r.Detail})");
    }),

    ("заморозка на 16 КБ → TransferStalled + почерк ТСПУ", async () =>
    {
        await using var srv = FakeServer.Https(async (s, ct) =>
        {
            await FakeServer.WriteAsync(s, "HTTP/1.1 200 OK\r\nContent-Length: 65536\r\n\r\n", ct);
            await FakeServer.WriteBodyAsync(s, 16 * 1024, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var r = await prober.ProbeAsync(T(srv.Port, minBytes: 65536));
        Expect(r, Verdict.Blocked, Failure.TransferStalled, ProbeStage.Transfer);
        Assert(r.Detail!.Contains("16 КБ"), $"detail должен упоминать 16 КБ: {r.Detail}");
    }),

    ("остановка на 48 КБ → TransferStalled без почерка ТСПУ", async () =>
    {
        await using var srv = FakeServer.Https(async (s, ct) =>
        {
            await FakeServer.WriteAsync(s, "HTTP/1.1 200 OK\r\n\r\n", ct);
            await FakeServer.WriteBodyAsync(s, 48 * 1024, ct);
            await Task.Delay(Timeout.Infinite, ct);
        });
        var r = await prober.ProbeAsync(T(srv.Port, minBytes: 65536));
        Expect(r, Verdict.Blocked, Failure.TransferStalled, ProbeStage.Transfer);
        Assert(!r.Detail!.Contains("16 КБ"), $"не должно быть почерка ТСПУ: {r.Detail}");
    }),

    ("64 КБ отдались целиком → Ok", async () =>
    {
        await using var srv = FakeServer.Https(async (s, ct) =>
        {
            await FakeServer.WriteAsync(s, "HTTP/1.1 200 OK\r\nContent-Length: 65536\r\n\r\n", ct);
            await FakeServer.WriteBodyAsync(s, 65536, ct);
        });
        var r = await prober.ProbeAsync(T(srv.Port, minBytes: 65536));
        Expect(r, Verdict.Ok, Failure.None, null);
        Assert(r.HttpStatus == 200 && r.BodyBytes >= 65536 && r.Kbps > 0, $"status={r.HttpStatus} body={r.BodyBytes} kbps={r.Kbps}");
    }),

    ("медленная отдача → Throttled", async () =>
    {
        await using var srv = FakeServer.Https(async (s, ct) =>
        {
            await FakeServer.WriteAsync(s, "HTTP/1.1 200 OK\r\n\r\n", ct);
            for (var i = 0; i < 8; i++) { await FakeServer.WriteBodyAsync(s, 1024, ct); await Task.Delay(100, ct); }
        });
        var r = await prober.ProbeAsync(T(srv.Port, minBytes: 8 * 1024, minKbps: 10_000));
        Expect(r, Verdict.Throttled, Failure.Slow, ProbeStage.Transfer);
    }),

    ("HTTP 451 → HttpBlockPage", async () =>
    {
        await using var srv = FakeServer.Https((s, ct) =>
            FakeServer.WriteAsync(s, "HTTP/1.1 451 Unavailable For Legal Reasons\r\n\r\n", ct));
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.HttpBlockPage, ProbeStage.Http);
    }),

    ("редирект на warning.rt.ru → HttpBlockPage", async () =>
    {
        await using var srv = FakeServer.Https((s, ct) =>
            FakeServer.WriteAsync(s, "HTTP/1.1 302 Found\r\nLocation: http://warning.rt.ru/?id=1\r\n\r\n", ct));
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Blocked, Failure.HttpBlockPage, ProbeStage.Http);
    }),

    ("обычный редирект → Ok", async () =>
    {
        await using var srv = FakeServer.Https((s, ct) =>
            FakeServer.WriteAsync(s, "HTTP/1.1 301 Moved\r\nLocation: https://www.example.test/\r\n\r\n", ct));
        var r = await prober.ProbeAsync(T(srv.Port));
        Expect(r, Verdict.Ok, Failure.None, null);
    }),

    ("закрытый порт → TcpRefused", async () =>
    {
        var r = await prober.ProbeAsync(T(FreePort()));
        Expect(r, Verdict.Blocked, Failure.TcpRefused, ProbeStage.Tcp);
    }),

    ("DNS вернул 0.0.0.0 → DnsBogus", async () =>
    {
        var p = new ServiceProber(new ProbeOptions { Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Any }) });
        var r = await p.ProbeAsync(T(443));
        Expect(r, Verdict.Blocked, Failure.DnsBogus, ProbeStage.Dns);
    }),

    ("нет сети (контрольные лежат) → Inconclusive", async () =>
    {
        var port = FreePort();
        var results = await new ProbeRunner(prober).RunAsync([T(port, control: true), T(port)]);
        Assert(results.All(r => r.Verdict == Verdict.Inconclusive),
            string.Join(", ", results.Select(r => r.Verdict)));
    }),
};

tests.AddRange(TunerTests.All(opts));
tests.AddRange(EngineTests.All());
tests.AddRange(ControlTests.All());
tests.AddRange(AppTests.All());
tests.AddRange(SiteCheckTests.All(opts));
tests.AddRange(AppUpdateTests.All());
tests.AddRange(TelegramTests.All());
tests.AddRange(DnsTests.All());
tests.AddRange(ObserveTests.All());
tests.AddRange(VoiceTests.All());

var failed = 0;
foreach (var (name, body) in tests)
{
    try
    {
        await body();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"FAIL  {name}\n      {ex.Message}");
    }
    Console.ResetColor();
}
Console.WriteLine($"\n{tests.Count - failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;

static void Expect(ProbeResult r, Verdict v, Failure f, ProbeStage? at) =>
    Assert(r.Verdict == v && r.Failure == f && r.FailedAt == at,
        $"ожидали {v}/{f}/{at?.ToString() ?? "-"}, получили {r.Verdict}/{r.Failure}/{r.FailedAt?.ToString() ?? "-"} ({r.Detail})");

static void Assert(bool ok, string message)
{
    if (!ok) throw new Exception(message);
}

static int FreePort()
{
    var l = new TcpListener(IPAddress.Loopback, 0);
    l.Start();
    var port = ((IPEndPoint)l.LocalEndpoint).Port;
    l.Stop();
    return port;
}
