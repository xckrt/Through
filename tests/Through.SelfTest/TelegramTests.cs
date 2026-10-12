using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Through.Core.Control;
using Through.Core.Probing;
using Through.Core.Telegram;
using Through.Core.Tuning;

namespace Through.SelfTest;

/// <summary>Прокси Telegram: шифр, init, резка пакетов и сквозной путь через поддельный DC на loopback.</summary>
internal static class TelegramTests
{
    private static readonly byte[] Secret = Convert.FromHexString("0123456789abcdef0123456789abcdef");

    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("AES-256-CTR: вектор NIST SP 800-38A F.5.5, кусками и целиком одинаково", () =>
        {
            var key = Convert.FromHexString("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4");
            var iv = Convert.FromHexString("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff");
            var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
            using var whole = new AesCtr(key, iv);
            var c = whole.TransformCopy(plain);
            Assert(Convert.ToHexString(c).ToLowerInvariant() == "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5",
                Convert.ToHexString(c));

            // счётчик переходит через границу пачки (64 блока) и через перенос байта
            var big = RandomNumberGenerator.GetBytes(5000);
            using var a = new AesCtr(key, iv);
            using var b = new AesCtr(key, iv);
            var once = a.TransformCopy(big);
            var parts = (byte[])big.Clone();
            for (int at = 0, step = 1; at < parts.Length; at += step, step = step * 3 % 1031 + 1)
                b.Transform(parts.AsSpan(at, Math.Min(step, parts.Length - at)));
            Assert(once.AsSpan().SequenceEqual(parts), "гамма кусками разошлась с гаммой целиком");
            return Task.CompletedTask;
        }),

        ("init: DC, медиа и транспорт читаются с верным секретом, с чужим — нет", () =>
        {
            var init = MtProto.BuildInit(MtProtoTransport.Abridged, -4, Secret);
            var hello = MtProto.TryParseClientInit(init, Secret);
            Assert(hello is { Dc: 4, IsMedia: true, IsTest: false, Transport: MtProtoTransport.Abridged }, $"{hello}");
            Assert(hello!.DcIndex == -4, $"DcIndex {hello.DcIndex}");
            Assert(MtProto.TryParseClientInit(init, RandomNumberGenerator.GetBytes(16)) is null, "чужой секрет не должен проходить");

            var test = MtProto.TryParseClientInit(MtProto.BuildInit(MtProtoTransport.Intermediate, 10002, Secret), Secret);
            Assert(test is { Dc: 2, IsTest: true, IsMedia: false }, $"тестовый DC: {test}");
            return Task.CompletedTask;
        }),

        ("init к Telegram: ключи без секрета, ответ расшифровывается обратным ключом", () =>
        {
            var relay = MtProto.BuildInit(MtProtoTransport.PaddedIntermediate, 2);
            Assert(relay[0] != 0xEF && !relay.AsSpan(4, 4).SequenceEqual(new byte[4]), "зарезервированное начало init");
            var (send, recv) = MtProto.InitiatorCiphers(relay);
            var (dec, enc) = MtProto.ResponderCiphers(relay);
            var msg = Encoding.ASCII.GetBytes("ping-pong-ping-pong");
            Assert(dec.TransformCopy(send.TransformCopy(msg)).AsSpan().SequenceEqual(msg), "туда");
            Assert(recv.TransformCopy(enc.TransformCopy(msg)).AsSpan().SequenceEqual(msg), "обратно");
            return Task.CompletedTask;
        }),

        ("Резка: пакеты intermediate и abridged расходятся по одному на кадр, даже если пришли кусками", () =>
        {
            byte[] Inter(int len) { var p = new byte[4 + len]; BinaryPrimitives.WriteInt32LittleEndian(p, len); return p; }
            var stream = Inter(8).Concat(Inter(12)).Concat(Inter(4)).ToArray();
            var s = new MsgSplitter(MtProtoTransport.Intermediate);
            var got = new List<int>();
            foreach (var chunk in stream.Chunk(5)) got.AddRange(s.Split(chunk, chunk).Select(p => p.Length));
            Assert(got.SequenceEqual([12, 16, 8]), string.Join(",", got));

            var ab = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0x7F, 1, 0, 0, 1, 2, 3, 4 };
            var sa = new MsgSplitter(MtProtoTransport.Abridged);
            var parts = sa.Split(ab, ab).Select(p => p.Length).ToArray();
            Assert(parts.SequenceEqual([9, 8]), string.Join(",", parts));
            return Task.CompletedTask;
        }),

        ("Прокси: req_pq_multi доходит до «DC» через WebSocket и возвращается resPQ", async () =>
        {
            await using var dc = new FakeDc(webSocket: true);
            await using var proxy = new TelegramProxy(Local(dc) with { TcpFallback = false });
            proxy.Start();
            var r = await TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, Secret, 2, TimeSpan.FromSeconds(10));
            Assert(r.Ok, r.Error ?? "");
            Assert(dc.Hosts.Contains("kws2.web.telegram.org /apiws"), $"Host и путь: {string.Join("; ", dc.Hosts)}");
            Assert(dc.SeenDc.Contains(2), "DC в init к Telegram");
            Assert(dc.MaxPacketsPerFrame == 1, $"в кадре {dc.MaxPacketsPerFrame} пакетов, нужен один");
            var st = proxy.Stats();
            Assert(st.ViaWebSocket == 1 && st.LastRoute == "DC2 → WebSocket", $"{st}");
        }),

        ("Прокси: без WebSocket и Cloudflare уходит напрямую по TCP в DC", async () =>
        {
            await using var dc = new FakeDc(webSocket: false);
            await using var proxy = new TelegramProxy(Local(null) with
            {
                TcpFallback = true,
                DcAddresses = new Dictionary<int, string> { [1] = "127.0.0.1" },
                TcpPort = dc.Port,
            });
            proxy.Start();
            var r = await TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, Secret, 1, TimeSpan.FromSeconds(10));
            Assert(r.Ok, r.Error ?? "");
            Assert(proxy.Stats().ViaTcp == 1, $"{proxy.Stats()}");
        }),

        ("Прокси: чужой секрет отбивается и считается, путей нет — честная ошибка", async () =>
        {
            await using var proxy = new TelegramProxy(Local(null) with { TcpFallback = false });
            proxy.Start();
            var wrong = await TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, RandomNumberGenerator.GetBytes(16), 2, TimeSpan.FromSeconds(5));
            Assert(!wrong.Ok && proxy.Stats().BadSecret == 1, $"{wrong} {proxy.Stats()}");
            var none = await TelegramProbe.CheckAsync("127.0.0.1", proxy.Port, Secret, 3, TimeSpan.FromSeconds(5));
            Assert(!none.Ok && proxy.Stats().Failed == 1, $"{none} {proxy.Stats()}");
        }),

        ("Служба: прокси переживает выключение и перезапуск с той же ссылкой, занятый порт обходит", async () =>
        {
            await using var dc = new FakeDc(webSocket: true);
            using var squatter = new TcpListener(IPAddress.Loopback, 0);
            squatter.Start();
            var busy = ((IPEndPoint)squatter.LocalEndpoint).Port;
            var dir = Path.Combine(Path.GetTempPath(), $"through-tg-{Guid.NewGuid():N}");
            try
            {
                ControllerOptions Opts() => new()
                {
                    Targets = new TargetCatalog { Services = [] },
                    Presets = new PresetCatalog { Presets = [] },
                    Engine = _ => throw new InvalidOperationException("движок в этом тесте не нужен"),
                    DataDir = dir,
                    TelegramPort = busy,
                    TelegramTemplate = Local(dc) with { Secret = new byte[16], TcpFallback = false },
                };

                string? link;
                await using (var c = new ThroughController(Opts()))
                {
                    await c.StartAsync();
                    var on = await c.SetTelegramAsync(true);
                    Assert(on.Telegram is { Enabled: true, Running: true } t && t.Port != busy, $"порт {on.Telegram?.Port}, занятый {busy}");
                    Assert(on.Message!.Contains("занят"), on.Message);
                    link = on.Telegram!.Link;
                    Assert(link is not null && link.Contains($"port={on.Telegram.Port}") && link.Contains("secret=dd"), link ?? "");

                    var checkedStatus = await c.CheckTelegramAsync();
                    Assert(checkedStatus.Telegram!.Stats is { Total: 0, LastConnectionAt: null },
                        $"своя проверка не должна считаться подключением Telegram: {checkedStatus.Telegram.Stats}");
                    Assert(checkedStatus.Telegram!.Checks.Single(x => x.Dc == 2).Ok, string.Join("; ", checkedStatus.Telegram.Checks.Select(x => x.ToString())));

                    var off = await c.SetTelegramAsync(false);
                    Assert(off.Telegram is { Enabled: false, Running: false }, "выключение");
                    var again = await c.SetTelegramAsync(true);
                    Assert(again.Telegram!.Link == link, "после выключения ссылка должна остаться прежней");
                }

                await using (var c = new ThroughController(Opts()))
                {
                    await c.StartAsync();
                    var st = c.Status().Telegram!;
                    Assert(st is { Enabled: true, Running: true } && st.Link == link, $"после перезапуска: {st.Link} vs {link}");
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }),

        ("QR: матрицы совпадают с эталоном, проверенным декодером zxing (байтовый режим, M)", () =>
        {
            static string Flat(bool[,] m) { var sb = new StringBuilder(); foreach (var x in m) sb.Append(x ? '1' : '0'); return sb.ToString(); }
            static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(s))).ToLowerInvariant();
            var hello = QrCode.Encode("hello", 0);
            Assert(hello.GetLength(0) == 21 && Hash(Flat(hello)) == "99ccedcf0d82e92a63894c8b42e405a3ac50eafa33ee2ce68fa895813fdcb386", "hello, маска 0");
            var link = QrCode.Encode("https://t.me/proxy?server=192.168.1.42&port=1443&secret=dd0123456789abcdef0123456789abcdef");
            Assert(link.GetLength(0) == 41 && Hash(Flat(link)) == "6e744202499893db34800104a46986b65420efcb5c42d29fbe2c5f60ede96f26", $"ссылка: {link.GetLength(0)}");
            return Task.CompletedTask;
        }),

        ("Телефон: прокси слушает домашнюю сеть, порт открывается и закрывается в брандмауэре", async () =>
        {
            var calls = new List<(int, bool)>();
            var dir = Path.Combine(Path.GetTempPath(), $"through-lan-{Guid.NewGuid():N}");
            try
            {
                await using var c = new ThroughController(new ControllerOptions
                {
                    Targets = new TargetCatalog { Services = [] },
                    Presets = new PresetCatalog { Presets = [] },
                    Engine = _ => throw new InvalidOperationException(),
                    DataDir = dir,
                    TelegramPort = 0,
                    TelegramTemplate = Local(null) with { Secret = new byte[16], TcpFallback = false },
                    Firewall = (_, port, open, _) => { lock (calls) calls.Add((port, open)); return Task.CompletedTask; },
                });
                await c.StartAsync();
                await c.SetTelegramAsync(true);
                var s = await c.SetTelegramLanAsync(true);
                var port = s.Telegram!.Port;
                Assert(s.Telegram.Lan && calls.Contains((port, true)), $"порт {port} в брандмауэр: {string.Join(",", calls)}");
                var lan = ThroughController.LanAddress();
                Assert(lan is null ? s.Telegram.LanLink is null : s.Telegram.LanLink!.StartsWith($"https://t.me/proxy?server={lan}&port={port}&secret=dd"), s.Telegram.LanLink ?? "нет ссылки");
                if (lan is not null)
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(lan, port);
                    Assert(probe.Connected, "прокси должен принимать соединения из сети");
                }
                await c.SetTelegramAsync(false);
                Assert(calls[^1] == (port, false), "выключение закрывает порт");
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }),

        ("CloudflareDomains: расшифровка как в tg-ws-proxy, мусорные имена отсеиваются", () =>
        {
            Assert(CloudflareDomains.Decode("abc.com") == "xyz.co.uk", CloudflareDomains.Decode("abc.com"));
            Assert(CloudflareDomains.Decode("plain.org") == "plain.org", "не .com — как есть");
            var all = new CloudflareDomains().All;
            Assert(all.Count == 20 && all.All(d => d.EndsWith(".co.uk") && CloudflareDomains.IsValidDomain(d)), string.Join(",", all));
            Assert(!CloudflareDomains.IsValidDomain("-bad.com") && !CloudflareDomains.IsValidDomain("nodot"), "валидация");
            var cf = new CloudflareDomains(["a.example", "b.example", "c.example"]);
            cf.Worked(2, "b.example");
            Assert(cf.For(2)[0] == "b.example", "сработавший домен должен идти первым");
            return Task.CompletedTask;
        }),
    ];

    /// <summary>Прокси, который ходит только на loopback: DC2 → поддельный DC, без Cloudflare и фронтинга.</summary>
    private static TelegramProxyOptions Local(FakeDc? dc) => new()
    {
        Secret = Secret,
        Port = 0,
        WsRedirects = dc is null ? new Dictionary<int, string>() : new Dictionary<int, string> { [2] = "127.0.0.1" },
        WsPort = dc?.Port ?? 1,
        ValidateCertificate = false,
        FrontingSni = null,
        PoolSize = 1,
        UseCloudflare = false,
        CloudflareListUrl = null,
        DcAddresses = new Dictionary<int, string>(),
        ConnectTimeout = TimeSpan.FromSeconds(3),
    };

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    /// <summary>
    /// Поддельный DC: принимает init без секрета (как настоящий Telegram), расшифровывает intermediate-пакеты
    /// и на req_pq_multi отвечает resPQ с тем же nonce. С webSocket — TLS и /apiws, без — голый TCP.
    /// </summary>
    private sealed class FakeDc : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly bool _ws;
        private int _maxPerFrame;

        public ConcurrentBag<string> Hosts { get; } = [];
        public ConcurrentBag<int> SeenDc { get; } = [];
        public int MaxPacketsPerFrame => _maxPerFrame;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public FakeDc(bool webSocket)
        {
            _ws = webSocket;
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var c = await _listener.AcceptTcpClientAsync(_cts.Token);
                        _ = Task.Run(() => ServeAsync(c, _cts.Token));
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
            });
        }

        private async Task ServeAsync(TcpClient client, CancellationToken ct)
        {
            using var _ = client;
            try
            {
                Stream s = client.GetStream();
                if (_ws)
                {
                    var ssl = new SslStream(s);
                    await ssl.AuthenticateAsServerAsync(Cert.Value);
                    s = ssl;
                    var head = await ReadHeadAsync(s, ct);
                    var lines = head.Split("\r\n");
                    var path = lines[0].Split(' ')[1];
                    var host = lines.First(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))[5..].Trim();
                    Hosts.Add($"{host} {path}");
                    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n"), ct);
                }

                var init = _ws ? await ReadFrameAsync(s, ct) : await ReadAsync(s, 64, ct);
                var hello = MtProto.TryParseClientInit(init, default) ?? throw new IOException("init не читается без секрета");
                SeenDc.Add(hello.Dc);
                var (dec, enc) = MtProto.ResponderCiphers(init);
                var pending = new List<byte>();
                while (true)
                {
                    var chunk = _ws ? await ReadFrameAsync(s, ct) : await ReadSomeAsync(s, ct);
                    dec.Transform(chunk.AsSpan());
                    pending.AddRange(chunk);
                    var inFrame = 0;
                    while (pending.Count >= 4)
                    {
                        var len = BinaryPrimitives.ReadInt32LittleEndian(pending.Take(4).ToArray());
                        if (pending.Count < 4 + len) break;
                        var body = pending.Skip(4).Take(len).ToArray();
                        pending.RemoveRange(0, 4 + len);
                        inFrame++;
                        if (BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) != 0xbe7e8ef1) continue;

                        var reply = new byte[4 + 56];
                        BinaryPrimitives.WriteInt32LittleEndian(reply, 56);
                        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(4 + 16), 36);
                        BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4 + 20), 0x05162463);
                        body.AsSpan(24, 16).CopyTo(reply.AsSpan(4 + 24));
                        enc.Transform(reply.AsSpan());
                        if (_ws)
                        {
                            var frame = new byte[2 + reply.Length];
                            frame[0] = 0x82;
                            frame[1] = (byte)reply.Length;
                            reply.CopyTo(frame, 2);
                            await s.WriteAsync(frame, ct);
                        }
                        else await s.WriteAsync(reply, ct);
                    }
                    if (_ws) InterlockedMax(ref _maxPerFrame, inFrame);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or EndOfStreamException) { }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int cur;
            while (value > (cur = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, cur) != cur) { }
        }

        private static async Task<string> ReadHeadAsync(Stream s, CancellationToken ct)
        {
            var sb = new StringBuilder();
            var one = new byte[1];
            while (!sb.ToString().EndsWith("\r\n\r\n"))
            {
                await s.ReadExactlyAsync(one, ct);
                sb.Append((char)one[0]);
            }
            return sb.ToString();
        }

        private static async Task<byte[]> ReadAsync(Stream s, int n, CancellationToken ct)
        {
            var b = new byte[n];
            await s.ReadExactlyAsync(b, ct);
            return b;
        }

        private static async Task<byte[]> ReadSomeAsync(Stream s, CancellationToken ct)
        {
            var b = new byte[4096];
            var n = await s.ReadAsync(b, ct);
            if (n == 0) throw new EndOfStreamException();
            return b[..n];
        }

        // кадр от клиента: всегда с маской
        private static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct)
        {
            var h = await ReadAsync(s, 2, ct);
            long len = h[1] & 0x7F;
            if (len == 126) len = BinaryPrimitives.ReadUInt16BigEndian(await ReadAsync(s, 2, ct));
            else if (len == 127) len = (long)BinaryPrimitives.ReadUInt64BigEndian(await ReadAsync(s, 8, ct));
            var mask = (h[1] & 0x80) != 0 ? await ReadAsync(s, 4, ct) : null;
            var data = await ReadAsync(s, (int)len, ct);
            if (mask is null) throw new IOException("кадр клиента без маски");
            for (var i = 0; i < data.Length; i++) data[i] ^= mask[i & 3];
            return data;
        }

        private static readonly Lazy<X509Certificate2> Cert = new(() =>
        {
            using var key = RSA.Create(2048);
            var req = new CertificateRequest("CN=kws2.web.telegram.org", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        });

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            await _loop;
        }
    }
}
