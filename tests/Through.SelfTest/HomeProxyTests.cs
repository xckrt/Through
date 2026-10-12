using System.Net;
using System.Net.Sockets;
using System.Text;
using Through.Core.Control;
using Through.Core.Probing;
using Through.Core.Proxy;
using Through.Core.Tuning;

namespace Through.SelfTest;

/// <summary>Прокси для всего дома: PAC, HTTP CONNECT, SOCKS5, отказ адресам не из домашней сети.</summary>
internal static class HomeProxyTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Дом: PAC отправляет через прокси только домены обхода, остальное напрямую", async () =>
        {
            await using var proxy = Start(_ => true);
            using var http = new HttpClient();
            var pac = await http.GetStringAsync($"http://127.0.0.1:{proxy.Port}/proxy.pac");
            Assert(pac.Contains("\"youtube.com\"") && pac.Contains($"PROXY 127.0.0.1:{proxy.Port}; DIRECT") && pac.Contains("return \"DIRECT\""), pac);
        }),

        ("Дом: HTTP CONNECT и SOCKS5 доводят байты до сервера и обратно", async () =>
        {
            using var echo = new Echo();
            await using var proxy = Start(_ => true);

            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var s = c.GetStream();
                await s.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT echo.test:{echo.Port} HTTP/1.1\r\nHost: echo.test\r\n\r\n"));
                var head = await ReadHead(s);
                Assert(head.StartsWith("HTTP/1.1 200"), head);
                Assert(await RoundTrip(s, "ping-connect") == "ping-connect", "CONNECT: эхо");
            }

            using (var c = new TcpClient())
            {
                await c.ConnectAsync(IPAddress.Loopback, proxy.Port);
                var s = c.GetStream();
                await s.WriteAsync(new byte[] { 5, 1, 0 });
                var hello = new byte[2];
                await s.ReadExactlyAsync(hello);
                Assert(hello[1] == 0, "SOCKS5: без пароля");
                var name = Encoding.ASCII.GetBytes("echo.test");
                byte[] req = [5, 1, 0, 3, (byte)name.Length, .. name, (byte)(echo.Port >> 8), (byte)echo.Port];
                await s.WriteAsync(req);
                var reply = new byte[10];
                await s.ReadExactlyAsync(reply);
                Assert(reply[1] == 0, $"SOCKS5: ответ {reply[1]}");
                Assert(await RoundTrip(s, "ping-socks") == "ping-socks", "SOCKS5: эхо");
            }
            Assert(proxy.Stats().Total == 2 && proxy.Stats().BytesUp > 0, $"{proxy.Stats()}");
        }),

        ("Дом: адреса не из домашней сети отбиваются сразу", async () =>
        {
            await using var proxy = Start(_ => false);
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, proxy.Port);
            var n = await c.GetStream().ReadAsync(new byte[16]).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(n == 0 && proxy.Stats().Refused == 1, $"закрыто без ответа: {n}, отказов {proxy.Stats().Refused}");
            Assert(HomeProxyOptions.IsPrivate(IPAddress.Parse("192.168.1.5")) && !HomeProxyOptions.IsPrivate(IPAddress.Parse("8.8.8.8")), "частные адреса");
        }),

        ("Служба: прокси для дома включается, открывает порт в брандмауэре, в PAC — домены обхода без «не трогать»", async () =>
        {
            var calls = new List<(string, int, bool)>();
            var dir = Path.Combine(Path.GetTempPath(), $"through-home-{Guid.NewGuid():N}");
            try
            {
                await using var c = new ThroughController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = new() { ["svc"] = [] }, Domains = new() { ["svc"] = ["svc.test", "bank.test"] } },
                    Presets = new PresetCatalog { Presets = [] },
                    Engine = _ => throw new InvalidOperationException(),
                    DataDir = dir,
                    HomeProxyPort = 0,
                    HomeProxyTemplate = new HomeProxyOptions { Domains = () => [], Allowed = _ => true, BlockPrivateTargets = false },
                    Firewall = (rule, port, open, _) => { lock (calls) calls.Add((rule, port, open)); return Task.CompletedTask; },
                });
                await c.StartAsync();
                await c.ExcludeAsync("bank.test", add: true);
                var s = await c.SetHomeProxyAsync(true);
                Assert(s.Home is { Enabled: true, Running: true } h && calls.Contains(("Through home proxy", h.Port, true)), $"{string.Join(",", calls)}");
                using var http = new HttpClient();
                var pac = await http.GetStringAsync($"http://127.0.0.1:{s.Home!.Port}/proxy.pac");
                Assert(pac.Contains("\"svc.test\"") && !pac.Contains("bank.test"), pac);
                var off = await c.SetHomeProxyAsync(false);
                Assert(off.Home is { Running: false } && calls[^1].Item3 == false, "выключение закрывает порт");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }),
    ];

    private static HomeProxy Start(Func<IPAddress, bool> allowed)
    {
        var p = new HomeProxy(new HomeProxyOptions
        {
            Port = 0,
            Listen = IPAddress.Loopback,
            Domains = () => ["youtube.com", "discord.com"],
            Allowed = allowed,
            BlockPrivateTargets = false,
            Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }),
        });
        p.Start();
        return p;
    }

    private static async Task<string> ReadHead(NetworkStream s)
    {
        var sb = new StringBuilder();
        var b = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n"))
        {
            await s.ReadExactlyAsync(b);
            sb.Append((char)b[0]);
        }
        return sb.ToString();
    }

    private static async Task<string> RoundTrip(NetworkStream s, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await s.WriteAsync(bytes);
        var back = new byte[bytes.Length];
        await s.ReadExactlyAsync(back).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.ASCII.GetString(back);
    }

    /// <summary>TCP-эхо на loopback.</summary>
    private sealed class Echo : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

        public Echo()
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient c;
                    try { c = await _l.AcceptTcpClientAsync(_cts.Token); }
                    catch (Exception) { return; }
                    _ = Task.Run(async () =>
                    {
                        using var _ = c;
                        var buf = new byte[4096];
                        var s = c.GetStream();
                        int n;
                        try { while ((n = await s.ReadAsync(buf, _cts.Token)) > 0) await s.WriteAsync(buf.AsMemory(0, n), _cts.Token); }
                        catch (Exception) { }
                    });
                }
            });
        }

        public void Dispose()
        {
            _cts.Cancel();
            _l.Stop();
        }
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
