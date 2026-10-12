using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Through.Core.Telegram;

public sealed record TelegramProxyOptions
{
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1443;

    /// <summary>16 байт секрета прокси. Клиент получает его в ссылке как dd + hex.</summary>
    public required byte[] Secret { get; init; }

    /// <summary>DC → IP, на котором отвечают kws{dc}.web.telegram.org. Как в tg-ws-proxy: только 2 и 4,
    /// остальные DC через Cloudflare и прямой TCP.</summary>
    public IReadOnlyDictionary<int, string> WsRedirects { get; init; } = new Dictionary<int, string>
    {
        [2] = "149.154.167.220",
        [4] = "149.154.167.220",
    };

    public int WsPort { get; init; } = 443;
    public bool ValidateCertificate { get; init; } = true;

    /// <summary>Если вход по имени режут, пробовать TLS к тому же IP с чужим SNI.</summary>
    public string? FrontingSni { get; init; } = "sprinthost.ru";

    /// <summary>Готовых WebSocket на каждый DC (отдельно обычный и медиа).</summary>
    public int PoolSize { get; init; } = 3;
    public TimeSpan PoolMaxAge { get; init; } = TimeSpan.FromSeconds(100);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public bool UseCloudflare { get; init; } = true;
    /// <summary>null — встроенный список Flowseal и обновление из его репозитория.</summary>
    public IReadOnlyList<string>? CloudflareDomains { get; init; }
    public string? CloudflareListUrl { get; init; } = Telegram.CloudflareDomains.ListUrl;
    public int CloudflarePort { get; init; } = 443;
    /// <summary>Сколько доменов Cloudflare пробовать на одно соединение, прежде чем сдаться.</summary>
    public int CloudflareTries { get; init; } = 3;

    /// <summary>Последний запасной путь: MTProto напрямую на IP дата-центра.</summary>
    public bool TcpFallback { get; init; } = true;
    public IReadOnlyDictionary<int, string> DcAddresses { get; init; } = MtProto.DcAddresses;
    public IReadOnlyDictionary<int, string> TestDcAddresses { get; init; } = MtProto.TestDcAddresses;
    public int TcpPort { get; init; } = 443;
}

/// <summary>Счётчики для окна: сколько соединений и каким путём ушло.</summary>
public sealed record TelegramProxyStats
{
    public int Active { get; init; }
    public long Total { get; init; }
    public long ViaWebSocket { get; init; }
    public long ViaCloudflare { get; init; }
    public long ViaTcp { get; init; }
    public long Failed { get; init; }
    public long BadSecret { get; init; }
    public long BytesUp { get; init; }
    public long BytesDown { get; init; }
    /// <summary>Чем ушло последнее соединение: «DC2 → WebSocket» и т.п.</summary>
    public string? LastRoute { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastConnectionAt { get; init; }
}

/// <summary>
/// Локальный MTProto-прокси для Telegram: принимает соединения клиента на 127.0.0.1, расшифровывает его init,
/// узнаёт DC и уводит трафик в WebSocket Telegram Web (kws{dc}.web.telegram.org/apiws) — их ТСПУ режет реже,
/// чем прямой MTProto. Порт tg-ws-proxy от Flowseal (MIT) без Python: работает внутри службы.
/// Пути по порядку: WebSocket из пула → WebSocket через домены Cloudflare → прямой TCP в DC.
/// </summary>
public sealed class TelegramProxy : IAsyncDisposable
{
    private readonly TelegramProxyOptions _o;
    private readonly CloudflareDomains _cf;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<PoolKey, ConcurrentQueue<TgWebSocket>> _pool = new();
    private readonly ConcurrentDictionary<PoolKey, byte> _refilling = new();
    private readonly ConcurrentDictionary<PoolKey, (int Failures, DateTime RetryAfter)> _poolBackoff = new();
    private readonly ConcurrentDictionary<string, (int Failures, DateTime RetryAfter)> _tcpBackoff = new();
    private readonly ConcurrentDictionary<Task, byte> _clients = new();
    private readonly ConcurrentDictionary<int, byte> _selfPorts = new();
    private TcpListener? _listener;
    private Task? _accept, _maintenance;
    private volatile bool _frontingFirst;

    private int _active;
    private long _total, _ws, _viaCf, _tcp, _failed, _bad, _up, _down;
    private string? _lastRoute, _lastError;
    private DateTimeOffset? _lastAt;

    private readonly record struct PoolKey(int Dc, bool Media, bool Test);

    public TelegramProxy(TelegramProxyOptions options)
    {
        if (options.Secret.Length != 16) throw new ArgumentException("секрет прокси — 16 байт");
        _o = options;
        _cf = new CloudflareDomains(options.CloudflareDomains);
    }

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _o.Port;

    /// <summary>Ссылка, которую Telegram понимает как «добавить прокси».</summary>
    public string Link => LinkFor(_o.ListenAddress == "0.0.0.0" ? "127.0.0.1" : _o.ListenAddress, Port, _o.Secret);

    public static string LinkFor(string host, int port, byte[] secret) =>
        $"tg://proxy?server={host}&port={port}&secret=dd{Convert.ToHexString(secret).ToLowerInvariant()}";

    /// <summary>Соединение с этого локального порта — своя проверка: в счётчики Telegram оно не идёт.</summary>
    public void MarkSelfCheck(int localPort) => _selfPorts[localPort] = 0;

    public TelegramProxyStats Stats() => new()
    {
        Active = Volatile.Read(ref _active),
        Total = Interlocked.Read(ref _total),
        ViaWebSocket = Interlocked.Read(ref _ws),
        ViaCloudflare = Interlocked.Read(ref _viaCf),
        ViaTcp = Interlocked.Read(ref _tcp),
        Failed = Interlocked.Read(ref _failed),
        BadSecret = Interlocked.Read(ref _bad),
        BytesUp = Interlocked.Read(ref _up),
        BytesDown = Interlocked.Read(ref _down),
        LastRoute = _lastRoute,
        LastError = _lastError,
        LastConnectionAt = _lastAt,
    };

    /// <summary>Занять порт и начать принимать. Порт занят — SocketException (AddressAlreadyInUse).</summary>
    public void Start()
    {
        var listener = new TcpListener(IPAddress.Parse(_o.ListenAddress), _o.Port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start(128);
        _listener = listener;
        var ct = _cts.Token;
        _accept = Task.Run(() => AcceptLoopAsync(listener, ct));
        _maintenance = Task.Run(() => MaintenanceLoopAsync(ct));
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; } // клиент отвалился между SYN и accept

            var self = client.Client.RemoteEndPoint is IPEndPoint ep && _selfPorts.TryRemove(ep.Port, out _);
            var task = Task.Run(() => HandleClientAsync(client, self, ct));
            _clients[task] = 0;
            _ = task.ContinueWith(t => _clients.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    // ---------- одно соединение клиента

    private async Task HandleClientAsync(TcpClient client, bool self, CancellationToken ct)
    {
        // своя проверка идёт тем же путём, но счётчики и «последнее соединение» — только про Telegram
        var counted = !self;
        if (counted)
        {
            Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _total);
        }
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var stream = client.GetStream();
            var init = new byte[MtProto.InitLength];
            using (var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                initCts.CancelAfter(TimeSpan.FromSeconds(10));
                await stream.ReadExactlyAsync(init, initCts.Token);
            }

            var hello = MtProto.TryParseClientInit(init, _o.Secret);
            if (hello is null)
            {
                if (counted) Interlocked.Increment(ref _bad);
                _lastError = "клиент пришёл с другим секретом: переподключите прокси в Telegram по новой ссылке";
                return;
            }
            if (counted) _lastAt = DateTimeOffset.Now;

            var relayInit = MtProto.BuildInit(hello.Transport, hello.DcIndex);
            var (cltDec, cltEnc) = MtProto.ResponderCiphers(init, _o.Secret);
            var (tgEnc, tgDec) = MtProto.InitiatorCiphers(relayInit);
            using (cltDec) using (cltEnc) using (tgEnc) using (tgDec)
            {
                var ctx = new Crypto(cltDec, cltEnc, tgEnc, tgDec, counted);
                var tag = $"DC{hello.Dc}{(hello.IsMedia ? " медиа" : "")}{(hello.IsTest ? " тест" : "")}";

                // как в tg-ws-proxy: пул пуст — не ждём таймаутов прямого входа, а идём через Cloudflare;
                // прямой WebSocket «на месте» — когда и Cloudflare не ответил
                if (TakePooled(hello) is { } pooled)
                {
                    Route(ref _ws, $"{tag} → WebSocket", counted);
                    await BridgeWsAsync(stream, pooled, relayInit, ctx, new MsgSplitter(hello.Transport), ct);
                    return;
                }
                if (!hello.IsTest && _o.UseCloudflare && await OpenCloudflareAsync(hello.Dc, ct) is { } cf)
                {
                    Route(ref _viaCf, $"{tag} → Cloudflare", counted);
                    await BridgeWsAsync(stream, cf, relayInit, ctx, new MsgSplitter(hello.Transport), ct);
                    return;
                }
                if (await ConnectNowAsync(hello, ct) is { } ws)
                {
                    Route(ref _ws, $"{tag} → WebSocket", counted);
                    await BridgeWsAsync(stream, ws, relayInit, ctx, new MsgSplitter(hello.Transport), ct);
                    return;
                }
                if (_o.TcpFallback && await OpenTcpAsync(hello, ct) is { } tcp)
                {
                    Route(ref _tcp, $"{tag} → напрямую", counted);
                    await BridgeTcpAsync(stream, tcp, relayInit, ctx, ct);
                    return;
                }
                if (counted) Interlocked.Increment(ref _failed);
                _lastError = $"{tag}: ни WebSocket, ни Cloudflare, ни прямой путь не ответили";
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException)
        {
            // клиент закрыл соединение или служба останавливается
        }
        catch (Exception ex)
        {
            // одно сломанное соединение не должно ронять прокси
            if (counted) Interlocked.Increment(ref _failed);
            _lastError = $"соединение оборвалось: {ex.Message}";
        }
        finally
        {
            if (counted) Interlocked.Decrement(ref _active);
        }
    }

    private void Route(ref long counter, string route, bool counted)
    {
        if (!counted) return;
        Interlocked.Increment(ref counter);
        _lastRoute = route;
    }

    private sealed record Crypto(AesCtr CltDec, AesCtr CltEnc, AesCtr TgEnc, AesCtr TgDec, bool Counted);

    // ---------- пути до Telegram

    private TgWebSocket? TakePooled(ClientInit hello)
    {
        if (!_o.WsRedirects.ContainsKey(hello.Dc)) return null;
        var key = new PoolKey(hello.Dc, hello.IsMedia, hello.IsTest);
        var queue = _pool.GetOrAdd(key, _ => new ConcurrentQueue<TgWebSocket>());
        try
        {
            while (queue.TryDequeue(out var ws))
            {
                if (!IsStale(ws)) return ws;
                _ = ws.DisposeAsync().AsTask();
            }
            return null;
        }
        finally
        {
            ScheduleRefill(key);
        }
    }

    private async Task<TgWebSocket?> ConnectNowAsync(ClientInit hello, CancellationToken ct)
    {
        var key = new PoolKey(hello.Dc, hello.IsMedia, hello.IsTest);
        if (!_o.WsRedirects.ContainsKey(hello.Dc)) return null;
        if (_poolBackoff.TryGetValue(key, out var b) && DateTime.UtcNow < b.RetryAfter) return null;
        return await ConnectDirectAsync(key, ct);
    }

    private bool IsStale(TgWebSocket ws) => ws.IsClosed || DateTime.UtcNow - ws.CreatedUtc > _o.PoolMaxAge;

    private async Task<TgWebSocket?> ConnectDirectAsync(PoolKey key, CancellationToken ct)
    {
        if (!_o.WsRedirects.TryGetValue(key.Dc, out var address)) return null;
        var path = key.Test ? "/apiws_test" : "/apiws";
        foreach (var domain in MtProto.WsDomains(key.Dc, key.Media))
        {
            bool[] modes = _o.FrontingSni is null ? [false] : _frontingFirst ? [true, false] : [false, true];
            foreach (var fronted in modes)
            {
                try
                {
                    var ep = fronted
                        ? new WsEndpoint(address, _o.WsPort, domain, _o.FrontingSni!, ValidateCertificate: false, path)
                        : new WsEndpoint(address, _o.WsPort, domain, domain, _o.ValidateCertificate, path);
                    var ws = await TgWebSocket.ConnectAsync(ep, _o.ConnectTimeout, ct);
                    _frontingFirst = fronted;
                    return ws;
                }
                catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
                {
                    _lastError = $"WebSocket {domain}{(fronted ? " (фронтинг)" : "")}: {ex.Message}";
                }
            }
        }
        return null;
    }

    private void ScheduleRefill(PoolKey key)
    {
        if (_o.PoolSize <= 0 || _cts.IsCancellationRequested) return;
        if (_poolBackoff.TryGetValue(key, out var b) && DateTime.UtcNow < b.RetryAfter) return;
        if (!_refilling.TryAdd(key, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var queue = _pool.GetOrAdd(key, _ => new ConcurrentQueue<TgWebSocket>());
                var needed = _o.PoolSize - queue.Count;
                if (needed <= 0) return;
                var made = await Task.WhenAll(Enumerable.Range(0, needed).Select(_ => ConnectDirectAsync(key, _cts.Token)));
                var ok = 0;
                foreach (var ws in made.OfType<TgWebSocket>())
                {
                    if (_cts.IsCancellationRequested) { await ws.DisposeAsync(); continue; }
                    queue.Enqueue(ws);
                    ok++;
                }
                if (ok > 0) _poolBackoff.TryRemove(key, out _);
                else
                {
                    var failures = _poolBackoff.TryGetValue(key, out var old) ? old.Failures + 1 : 1;
                    var delay = TimeSpan.FromSeconds(Math.Min(2 * Math.Pow(2, Math.Min(failures - 1, 12)), 600));
                    _poolBackoff[key] = (failures, DateTime.UtcNow + delay);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _refilling.TryRemove(key, out _);
            }
        });
    }

    private async Task<TgWebSocket?> OpenCloudflareAsync(int dc, CancellationToken ct)
    {
        var tries = 0;
        foreach (var baseDomain in _cf.For(dc))
        {
            if (tries++ >= _o.CloudflareTries) break;
            var domain = $"kws{dc}.{baseDomain}";
            try
            {
                var ws = await TgWebSocket.ConnectAsync(new WsEndpoint(domain, _o.CloudflarePort, domain, domain, _o.ValidateCertificate),
                    _o.ConnectTimeout, ct);
                _cf.Worked(dc, baseDomain);
                return ws;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or System.Security.Authentication.AuthenticationException)
            {
                _lastError = $"Cloudflare для DC{dc}: {ex.Message}";
            }
        }
        return null;
    }

    private async Task<TcpClient?> OpenTcpAsync(ClientInit hello, CancellationToken ct)
    {
        var table = hello.IsTest ? _o.TestDcAddresses : _o.DcAddresses;
        if (!table.TryGetValue(hello.Dc, out var address)) return null;
        if (_tcpBackoff.TryGetValue(address, out var b) && DateTime.UtcNow < b.RetryAfter) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_o.ConnectTimeout);
            var tcp = await TgWebSocket.ConnectTcpAsync(address, _o.TcpPort, cts.Token);
            _tcpBackoff.TryRemove(address, out _);
            return tcp;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            var failures = _tcpBackoff.TryGetValue(address, out var old) ? old.Failures + 1 : 1;
            _tcpBackoff[address] = (failures, DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Min(failures - 1, 7)), 3600)));
            _lastError = $"прямой путь в DC{hello.Dc} ({address}): {ex.Message}";
            return null;
        }
    }

    // ---------- мосты

    private async Task BridgeWsAsync(NetworkStream client, TgWebSocket ws, byte[] relayInit, Crypto c, MsgSplitter splitter, CancellationToken ct)
    {
        await using var _ = ws;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await ws.SendAsync(relayInit, cts.Token);

        async Task Up()
        {
            var buf = new byte[64 * 1024];
            while (true)
            {
                var n = await client.ReadAsync(buf, cts.Token);
                if (n == 0)
                {
                    if (splitter.Flush() is { } tail) await ws.SendAsync(tail, cts.Token);
                    return;
                }
                if (c.Counted) Interlocked.Add(ref _up, n);
                var plain = c.CltDec.TransformCopy(buf.AsSpan(0, n));
                var cipher = c.TgEnc.TransformCopy(plain);
                var parts = splitter.Split(cipher, plain);
                if (parts.Count == 1) await ws.SendAsync(parts[0], cts.Token);
                else if (parts.Count > 1) await ws.SendAsync(parts, cts.Token);
            }
        }

        async Task Down()
        {
            while (await ws.ReceiveAsync(cts.Token) is { } msg)
            {
                if (c.Counted) Interlocked.Add(ref _down, msg.Length);
                c.TgDec.Transform(msg.AsSpan());
                c.CltEnc.Transform(msg.AsSpan());
                await client.WriteAsync(msg, cts.Token);
            }
        }

        await RunBothAsync(Up(), Down(), cts);
    }

    private async Task BridgeTcpAsync(NetworkStream client, TcpClient remote, byte[] relayInit, Crypto c, CancellationToken ct)
    {
        using var _ = remote;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var dc = remote.GetStream();
        await dc.WriteAsync(relayInit, cts.Token);

        async Task Pump(Stream from, Stream to, AesCtr dec, AesCtr enc, bool up)
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await from.ReadAsync(buf, cts.Token)) > 0)
            {
                if (!c.Counted) { }
                else if (up) Interlocked.Add(ref _up, n);
                else Interlocked.Add(ref _down, n);
                dec.Transform(buf.AsSpan(0, n));
                enc.Transform(buf.AsSpan(0, n));
                await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
            }
        }

        await RunBothAsync(Pump(client, dc, c.CltDec, c.TgEnc, true), Pump(dc, client, c.TgDec, c.CltEnc, false), cts);
    }

    // одна сторона закончилась — гасим вторую и ждём её, чтобы шифры не освободились под ногами
    private static async Task RunBothAsync(Task a, Task b, CancellationTokenSource cts)
    {
        await Task.WhenAny(a, b);
        cts.Cancel();
        try { await Task.WhenAll(a, b); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or EndOfStreamException) { }
    }

    // ---------- обслуживание пула и списка Cloudflare

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        foreach (var dc in _o.WsRedirects.Keys)
            foreach (var media in new[] { false, true })
                ScheduleRefill(new PoolKey(dc, media, false));

        var nextCfRefresh = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (_o.UseCloudflare && _o.CloudflareDomains is null && _o.CloudflareListUrl is { } url && DateTime.UtcNow >= nextCfRefresh)
            {
                nextCfRefresh = DateTime.UtcNow.AddHours(1);
                _ = _cf.RefreshAsync(url, ct);
            }
            foreach (var (key, queue) in _pool)
            {
                var keep = new List<TgWebSocket>();
                while (queue.TryDequeue(out var ws))
                    if (IsStale(ws)) _ = ws.DisposeAsync().AsTask();
                    else keep.Add(ws);
                foreach (var ws in keep) queue.Enqueue(ws);
                if (queue.Count < _o.PoolSize) ScheduleRefill(key);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _listener?.Stop();
        var pending = new List<Task>(_clients.Keys);
        if (_accept is not null) pending.Add(_accept);
        if (_maintenance is not null) pending.Add(_maintenance);
        try { await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException or SocketException) { }
        foreach (var queue in _pool.Values)
            while (queue.TryDequeue(out var ws))
                await ws.DisposeAsync();
        _cts.Dispose();
    }
}
