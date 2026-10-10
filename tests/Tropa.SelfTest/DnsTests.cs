using System.Net;
using System.Net.Sockets;
using Tropa.Core.Control;
using Tropa.Core.Doh;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>Защищённый DNS: разбор сообщений, кэш, переключение DoH-серверов, переключение системы и откат.</summary>
internal static class DnsTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("DNS: ключ вопроса без регистра, TTL из ответа, SERVFAIL сохраняет ID и вопрос", () =>
        {
            var q = DnsMessage.Query("YouTube.COM", id: 0x1234);
            Assert(DnsMessage.QuestionKey(q) == "youtube.com|1|1", DnsMessage.QuestionKey(q) ?? "null");
            var a = Answer(q, ttl: 42);
            Assert(DnsMessage.MinTtl(a) == 42, $"ttl {DnsMessage.MinTtl(a)}");
            Assert(DnsMessage.AnswerCount(a) == 1 && DnsMessage.Rcode(a) == 0, "ответ");
            var f = DnsMessage.ServFail(q);
            Assert(DnsMessage.Id(f) == 0x1234 && DnsMessage.Rcode(f) == 2 && DnsMessage.QuestionKey(f) == "youtube.com|1|1", "servfail");
            Assert(DnsMessage.QuestionKey([1, 2, 3]) is null && DnsMessage.MinTtl([1]) is null, "мусор не роняет разбор");
            return Task.CompletedTask;
        }),

        ("DNS: запрос по UDP уходит в DoH, ID клиента сохраняется, повтор — из кэша", async () =>
        {
            var doh = new FakeDoh();
            await using var server = new SecureDnsServer(Options(doh));
            server.Start();
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var q = DnsMessage.Query("example.com", id: 0xBEEF);
            for (var i = 0; i < 2; i++)
            {
                await udp.SendAsync(q, new IPEndPoint(IPAddress.Loopback, server.Port));
                var r = await udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert(DnsMessage.Id(r.Buffer) == 0xBEEF && DnsMessage.AnswerCount(r.Buffer) == 1, $"ответ {i}");
            }
            Assert(doh.Calls == 1 && server.Stats().CacheHits == 1, $"запросов в DoH {doh.Calls}, из кэша {server.Stats().CacheHits}");
            Assert(doh.SeenIds.All(id => id == 0), "в DoH ID должен уходить нулём (RFC 8484)");
        }),

        ("DNS: по TCP тоже отвечает (длина впереди сообщения)", async () =>
        {
            var doh = new FakeDoh();
            await using var server = new SecureDnsServer(Options(doh));
            server.Start();
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
            var s = tcp.GetStream();
            var q = DnsMessage.Query("tcp.example", id: 7);
            await s.WriteAsync(new[] { (byte)(q.Length >> 8), (byte)q.Length });
            await s.WriteAsync(q);
            var len = new byte[2];
            await s.ReadExactlyAsync(len).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var a = new byte[len[0] << 8 | len[1]];
            await s.ReadExactlyAsync(a);
            Assert(DnsMessage.Id(a) == 7 && DnsMessage.QuestionName(a) == "tcp.example", "ответ по TCP");
        }),

        ("DNS: первый DoH-сервер лежит — ответ от второго, и он становится первым; лежат все — SERVFAIL", async () =>
        {
            var doh = new FakeDoh { Down = ["https://a.test/dns-query"] };
            await using var server = new SecureDnsServer(Options(doh) with
            {
                Upstreams = [new("A", "https://a.test/dns-query"), new("B", "https://b.test/dns-query")],
            });
            var a = await server.ResolveAsync(DnsMessage.Query("one.example", id: 1));
            Assert(DnsMessage.AnswerCount(a) == 1 && server.CurrentUpstream.Name == "B", $"текущий {server.CurrentUpstream.Name}");
            doh.Down = ["https://a.test/dns-query", "https://b.test/dns-query"];
            var f = await server.ResolveAsync(DnsMessage.Query("two.example", id: 2));
            Assert(DnsMessage.Rcode(f) == 2 && DnsMessage.Id(f) == 2 && server.Stats().Failures == 1, "все лежат → SERVFAIL");
        }),

        ("Служба: DNS переключается только после проверки, сторож отдаёт прежний при отказе DoH и возвращает", async () =>
        {
            var doh = new FakeDoh();
            var sw = new FakeSwitch();
            var dir = Path.Combine(Path.GetTempPath(), $"tropa-dns-{Guid.NewGuid():N}");
            try
            {
                await using var c = new TropaController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = [] },
                    Presets = new PresetCatalog { Presets = [] },
                    Engine = _ => throw new InvalidOperationException("движок не нужен"),
                    DataDir = dir,
                    DnsSwitch = sw,
                    Dns = Options(doh),
                });
                await c.StartAsync();
                Assert(sw.Restores == 1, "при старте с выключенным DNS — откат возможных остатков");

                doh.Down = ["https://a.test/dns-query"];
                var s = await c.SetSecureDnsAsync(true);
                Assert(s.Dns is { Enabled: true, Active: false } && !sw.Pointed, "DoH не отвечает — систему не трогаем");

                doh.Down = [];
                await c.WatchdogTickAsync();
                Assert(sw.Pointed && c.Status().Dns!.Active, "DoH ожил — сторож переключил");

                doh.Down = ["https://a.test/dns-query"];
                await c.WatchdogTickAsync();
                Assert(sw.Pointed, "одна неудача — ещё не повод");
                await c.WatchdogTickAsync();
                Assert(!sw.Pointed && c.Status().Dns is { Enabled: true, Active: false }, "две подряд — вернуть прежний DNS");

                doh.Down = [];
                await c.WatchdogTickAsync();
                Assert(sw.Pointed, "заработал — снова через DoH");

                var off = await c.SetSecureDnsAsync(false);
                Assert(!sw.Pointed && off.Dns is { Enabled: false, Running: false }, "выключение возвращает прежний");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),
    ];

    private static SecureDnsOptions Options(FakeDoh doh) => new()
    {
        Listen = [IPAddress.Loopback],
        Port = 0,
        Upstreams = [new("A", "https://a.test/dns-query")],
        Handler = () => doh,
        UpstreamTimeout = TimeSpan.FromSeconds(2),
    };

    /// <summary>Ответ A 93.184.216.34 на вопрос.</summary>
    private static byte[] Answer(byte[] q, uint ttl = 300)
    {
        var a = new List<byte>(q);
        a[2] = 0x81; a[3] = 0x80;
        a[6] = 0; a[7] = 1;
        a.AddRange([0xC0, 0x0C, 0, 1, 0, 1, (byte)(ttl >> 24), (byte)(ttl >> 16), (byte)(ttl >> 8), (byte)ttl, 0, 4, 93, 184, 216, 34]);
        return [.. a];
    }

    private sealed class FakeDoh : HttpMessageHandler
    {
        public volatile string[] Down = [];
        public int Calls;
        public List<ushort> SeenIds { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down.Contains(request.RequestUri!.ToString())) throw new HttpRequestException("недоступен");
            Interlocked.Increment(ref Calls);
            var q = await request.Content!.ReadAsByteArrayAsync(ct);
            lock (SeenIds) SeenIds.Add(DnsMessage.Id(q));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Answer(q)) };
        }
    }

    private sealed class FakeSwitch : IDnsSwitch
    {
        public bool Pointed;
        public int Restores;

        public Task<int> PointToLocalAsync(CancellationToken ct)
        {
            Pointed = true;
            return Task.FromResult(1);
        }

        public Task RestoreAsync(CancellationToken ct)
        {
            Pointed = false;
            Restores++;
            return Task.CompletedTask;
        }
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
