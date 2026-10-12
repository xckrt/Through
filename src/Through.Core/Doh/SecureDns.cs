using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Through.Core.Doh;

/// <summary>Сервер DNS-over-HTTPS. Только по IP: системный DNS указывает на нас же, имя DoH-сервера не разрешить.</summary>
public sealed record DohUpstream(string Name, string Url);

public sealed record SecureDnsOptions
{
    public IReadOnlyList<IPAddress> Listen { get; init; } = [IPAddress.Loopback, IPAddress.IPv6Loopback];
    public int Port { get; init; } = 53;

    /// <summary>По порядку предпочтения; сработавший становится первым.</summary>
    public IReadOnlyList<DohUpstream> Upstreams { get; init; } =
    [
        new("Cloudflare", "https://1.1.1.1/dns-query"),
        new("Google", "https://8.8.8.8/dns-query"),
        new("Quad9", "https://9.9.9.9/dns-query"),
        new("Cloudflare", "https://1.0.0.1/dns-query"),
        new("Google", "https://8.8.4.4/dns-query"),
    ];

    public TimeSpan UpstreamTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int CacheSize { get; init; } = 4096;
    public TimeSpan MaxCacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Тесты подменяют транспорт: свой HttpMessageHandler вместо настоящего HTTPS.</summary>
    public Func<HttpMessageHandler>? Handler { get; init; }
}

public sealed record SecureDnsStats
{
    public long Queries { get; init; }
    public long CacheHits { get; init; }
    public long Failures { get; init; }
    public string? Upstream { get; init; }
    public int? LastLatencyMs { get; init; }
    public string? LastError { get; init; }
}

/// <summary>
/// Локальный DNS (UDP и TCP на 127.0.0.1 и ::1, порт 53), который отвечает через DoH.
/// Так провайдер не видит и не подменяет DNS-запросы: ответ приходит внутри HTTPS к 1.1.1.1 или 8.8.8.8.
/// Ответы кэшируются по наименьшему TTL; упавший сервер уступает место следующему.
/// </summary>
public sealed class SecureDnsServer : IAsyncDisposable
{
    private readonly SecureDnsOptions _o;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (byte[] Response, DateTime Expires)> _cache = new();
    private readonly List<UdpClient> _udp = [];
    private readonly List<TcpListener> _tcp = [];
    private readonly List<Task> _loops = [];
    private readonly object _orderLock = new();
    private List<DohUpstream> _order;
    private long _queries, _hits, _failures;
    private int? _lastMs;
    private string? _lastError;

    public SecureDnsServer(SecureDnsOptions options)
    {
        _o = options;
        _order = [.. options.Upstreams];
        _http = new HttpClient(options.Handler?.Invoke() ?? new SocketsHttpHandler
        {
            ConnectTimeout = options.UpstreamTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // системный прокси службе ни к чему, а пользовательский может ходить через тот же DNS
            UseProxy = false,
        })
        {
            Timeout = options.UpstreamTimeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public int Port => _udp.Count > 0 ? ((IPEndPoint)_udp[0].Client.LocalEndPoint!).Port : _o.Port;

    public SecureDnsStats Stats() => new()
    {
        Queries = Interlocked.Read(ref _queries),
        CacheHits = Interlocked.Read(ref _hits),
        Failures = Interlocked.Read(ref _failures),
        Upstream = CurrentUpstream.Name,
        LastLatencyMs = _lastMs,
        LastError = _lastError,
    };

    public DohUpstream CurrentUpstream
    {
        get { lock (_orderLock) return _order[0]; }
    }

    /// <summary>Открыть порты. Занято — SocketException; уже открытое при этом закрывается.</summary>
    public void Start()
    {
        try
        {
            var port = _o.Port;
            foreach (var addr in _o.Listen)
            {
                if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
                var udp = new UdpClient(addr.AddressFamily);
                udp.Client.ExclusiveAddressUse = true;
                udp.Client.Bind(new IPEndPoint(addr, port));
                // на Windows ICMP «порт недоступен» от прошлого ответа роняет следующий Receive: выключаем
                if (OperatingSystem.IsWindows()) udp.Client.IOControl(-1744830452, [0], null);
                _udp.Add(udp);
                if (port == 0) port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port; // тесты: второй адрес на тот же порт

                var tcp = new TcpListener(addr, port);
                tcp.Server.ExclusiveAddressUse = true;
                tcp.Start(64);
                _tcp.Add(tcp);
            }
        }
        catch
        {
            Close();
            throw;
        }
        foreach (var u in _udp) _loops.Add(Task.Run(() => UdpLoopAsync(u, _cts.Token)));
        foreach (var t in _tcp) _loops.Add(Task.Run(() => TcpLoopAsync(t, _cts.Token)));
    }

    private async Task UdpLoopAsync(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                var answer = await ResolveAsync(r.Buffer, ct);
                if (answer.Length > 1232 && (r.Buffer.Length < 12 || !HasEdns(r.Buffer)))
                    answer = Truncated(answer); // без EDNS клиент ждёт не больше 512 байт: пусть переспросит по TCP
                try { await udp.SendAsync(answer, r.RemoteEndPoint, ct); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
            }, ct);
        }
    }

    private static bool HasEdns(byte[] q) => q.Length >= 12 && (q[10] << 8 | q[11]) > 0;

    private static byte[] Truncated(byte[] answer)
    {
        var r = DnsMessage.ServFail(answer);
        r[2] |= 0x02; // TC
        r[3] = (byte)(r[3] & 0xF0);
        return r;
    }

    private async Task TcpLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(async () =>
            {
                using var _ = c;
                try
                {
                    var s = c.GetStream();
                    var len = new byte[2];
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(TimeSpan.FromSeconds(10));
                    while (true)
                    {
                        await s.ReadExactlyAsync(len, idle.Token);
                        var q = new byte[len[0] << 8 | len[1]];
                        await s.ReadExactlyAsync(q, idle.Token);
                        var a = await ResolveAsync(q, ct);
                        await s.WriteAsync(new[] { (byte)(a.Length >> 8), (byte)a.Length }, ct);
                        await s.WriteAsync(a, ct);
                    }
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException or ObjectDisposedException) { }
            }, ct);
        }
    }

    /// <summary>Ответ на запрос: из кэша или через DoH; всё упало — SERVFAIL (клиент спросит снова, а не повиснет).</summary>
    public Task<byte[]> ResolveAsync(byte[] query, CancellationToken ct = default) => ResolveAsync(query, useCache: true, ct);

    private async Task<byte[]> ResolveAsync(byte[] query, bool useCache, CancellationToken ct)
    {
        if (useCache) Interlocked.Increment(ref _queries);
        if (query.Length < DnsMessage.HeaderLength) return DnsMessage.ServFail(query);
        var id = DnsMessage.Id(query);
        var key = DnsMessage.QuestionKey(query);
        if (useCache && key is not null && _cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
        {
            Interlocked.Increment(ref _hits);
            var copy = (byte[])hit.Response.Clone();
            DnsMessage.SetId(copy, id);
            return copy;
        }

        // RFC 8484: ID в DoH лучше слать нулём, чтобы ответ кэшировался по дороге
        var wire = (byte[])query.Clone();
        DnsMessage.SetId(wire, 0);
        List<DohUpstream> order;
        lock (_orderLock) order = [.. _order];
        foreach (var up in order)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                using var content = new ByteArrayContent(wire);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
                using var req = new HttpRequestMessage(HttpMethod.Post, up.Url) { Content = content };
                req.Headers.Accept.ParseAdd("application/dns-message");
                using var resp = await _http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                var answer = await resp.Content.ReadAsByteArrayAsync(ct);
                if (answer.Length < DnsMessage.HeaderLength) throw new InvalidDataException("короткий ответ");
                _lastMs = (int)sw.ElapsedMilliseconds;
                Promote(up);
                if (key is not null && DnsMessage.Rcode(answer) is 0 or 3)
                {
                    var ttl = Math.Min(DnsMessage.MinTtl(answer) ?? 60, (uint)_o.MaxCacheTtl.TotalSeconds);
                    if (ttl > 0)
                    {
                        if (_cache.Count >= _o.CacheSize) _cache.Clear(); // грубо, зато без утечки
                        _cache[key] = (answer, DateTime.UtcNow.AddSeconds(ttl));
                    }
                }
                DnsMessage.SetId(answer, id);
                return answer;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException && !ct.IsCancellationRequested)
            {
                _lastError = $"{up.Name} ({up.Url}): {ex.Message}";
            }
        }
        if (useCache) Interlocked.Increment(ref _failures);
        return DnsMessage.ServFail(query);
    }

    private void Promote(DohUpstream up)
    {
        lock (_orderLock)
        {
            if (_order[0] == up) return;
            _order.Remove(up);
            _order.Insert(0, up);
        }
    }

    /// <summary>Проверка перед тем, как отдать системе: разрешается ли имя через DoH.</summary>
    public async Task<bool> SelfTestAsync(string name = "ya.ru", CancellationToken ct = default)
    {
        // мимо кэша: иначе закэшированный ответ «подтвердит» упавший DoH
        var a = await ResolveAsync(DnsMessage.Query(name, id: 0x7070), useCache: false, ct);
        return DnsMessage.Rcode(a) == 0 && DnsMessage.AnswerCount(a) > 0;
    }

    private void Close()
    {
        foreach (var u in _udp) u.Dispose();
        foreach (var t in _tcp) t.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        Close();
        try { await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or SocketException or ObjectDisposedException) { }
        _http.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Переключение DNS системы на локальный сервер и обратно. Реализация — в службе (netsh), в тестах — подделка.</summary>
public interface IDnsSwitch
{
    /// <summary>Указать адаптерам 127.0.0.1 / ::1, запомнив прежние настройки. Возвращает, сколько адаптеров переключено.</summary>
    Task<int> PointToLocalAsync(CancellationToken ct);

    /// <summary>Вернуть то, что было до Сквозь.</summary>
    Task RestoreAsync(CancellationToken ct);
}
