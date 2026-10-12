using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Through.Core.Proxy;

public sealed record HomeProxyOptions
{
    public int Port { get; init; } = 3128;
    public IPAddress Listen { get; init; } = IPAddress.Any;

    /// <summary>Домены, которые идут через Тропу (для PAC). Остальное телефон отправляет напрямую.</summary>
    public required Func<IReadOnlyList<string>> Domains { get; init; }

    /// <summary>Кого пускать: по умолчанию только частные адреса (дом, а не интернет). Тесты пускают loopback.</summary>
    public Func<IPAddress, bool> Allowed { get; init; } = IsPrivate;

    public Func<string, CancellationToken, Task<IPAddress[]>>? Resolve { get; init; }

    /// <summary>Не ходить через прокси в частные сети (роутер, NAS соседа по сети): это не туннель в локалку.</summary>
    public bool BlockPrivateTargets { get; init; } = true;
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6) return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }
}

public sealed record HomeProxyStats(int Active, long Total, long Refused, long BytesUp, long BytesDown, int Devices);

/// <summary>
/// Прокси для домашней сети: HTTP (CONNECT и обычные запросы) и SOCKS5 на одном порту, плюс /proxy.pac.
/// Соединения телефона уходят с этого компьютера, поэтому их видит winws и обходит так же, как свои.
/// PAC отправляет через Тропу только домены из обхода, остальное телефон гонит напрямую.
/// Пускает только адреса из частных сетей.
/// </summary>
public sealed class HomeProxy : IAsyncDisposable
{
    private readonly HomeProxyOptions _o;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _loop;
    private int _active;
    private long _total, _refused, _up, _down;
    private readonly HashSet<string> _devices = [];

    public HomeProxy(HomeProxyOptions options) => _o = options;

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _o.Port;

    public HomeProxyStats Stats()
    {
        lock (_devices)
            return new(Volatile.Read(ref _active), Interlocked.Read(ref _total), Interlocked.Read(ref _refused),
                Interlocked.Read(ref _up), Interlocked.Read(ref _down), _devices.Count);
    }

    public void Start()
    {
        var l = new TcpListener(_o.Listen, _o.Port);
        l.Server.ExclusiveAddressUse = true;
        l.Start(128);
        _listener = l;
        _loop = Task.Run(() => AcceptAsync(l, _cts.Token));
    }

    private async Task AcceptAsync(TcpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(ct); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            var remote = (c.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !_o.Allowed(remote))
            {
                Interlocked.Increment(ref _refused);
                c.Dispose();
                continue;
            }
            lock (_devices) _devices.Add(remote.ToString());
            _ = Task.Run(() => ServeAsync(c, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _active);
        Interlocked.Increment(ref _total);
        try
        {
            using var _ = client;
            client.NoDelay = true;
            var s = client.GetStream();
            var first = new byte[1];
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(TimeSpan.FromSeconds(15));
                await s.ReadExactlyAsync(first, t.Token);
            }
            if (first[0] == 5) await SocksAsync(s, ct);
            else await HttpAsync(s, first[0], ct);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or EndOfStreamException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    // ---------- SOCKS5 (RFC 1928), только CONNECT без пароля

    private async Task SocksAsync(NetworkStream s, CancellationToken ct)
    {
        var n = new byte[1];
        await s.ReadExactlyAsync(n, ct);
        var methods = new byte[n[0]];
        await s.ReadExactlyAsync(methods, ct);
        if (!methods.Contains((byte)0)) { await s.WriteAsync(new byte[] { 5, 0xFF }, ct); return; }
        await s.WriteAsync(new byte[] { 5, 0 }, ct);

        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct);
        if (head[0] != 5 || head[1] != 1) { await SocksReply(s, 7, ct); return; } // только CONNECT
        string host;
        switch (head[3])
        {
            case 1:
                var v4 = new byte[4]; await s.ReadExactlyAsync(v4, ct); host = new IPAddress(v4).ToString(); break;
            case 4:
                var v6 = new byte[16]; await s.ReadExactlyAsync(v6, ct); host = new IPAddress(v6).ToString(); break;
            case 3:
                var len = new byte[1]; await s.ReadExactlyAsync(len, ct);
                var name = new byte[len[0]]; await s.ReadExactlyAsync(name, ct); host = Encoding.ASCII.GetString(name); break;
            default:
                await SocksReply(s, 8, ct); return;
        }
        var portBytes = new byte[2];
        await s.ReadExactlyAsync(portBytes, ct);
        var port = BinaryPrimitives.ReadUInt16BigEndian(portBytes);

        using var remote = await ConnectAsync(host, port, ct);
        if (remote is null) { await SocksReply(s, 4, ct); return; }
        await SocksReply(s, 0, ct);
        await PipeAsync(s, remote.GetStream(), ReadOnlyMemory<byte>.Empty, ct);
    }

    private static Task SocksReply(NetworkStream s, byte code, CancellationToken ct) =>
        s.WriteAsync(new byte[] { 5, code, 0, 1, 0, 0, 0, 0, 0, 0 }, ct).AsTask();

    // ---------- HTTP: CONNECT, обычные запросы по абсолютному адресу и /proxy.pac

    private async Task HttpAsync(NetworkStream s, byte first, CancellationToken ct)
    {
        var head = new List<byte> { first };
        var b = new byte[1];
        while (!EndsWithBlankLine(head))
        {
            await s.ReadExactlyAsync(b, ct);
            head.Add(b[0]);
            if (head.Count > 64 * 1024) throw new InvalidDataException("заголовки длиннее 64 КБ");
        }
        var text = Encoding.ASCII.GetString(head.ToArray());
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 3) return;
        var (method, target) = (parts[0], parts[1]);

        if (method == "CONNECT")
        {
            var (host, port) = SplitHostPort(target, 443);
            using var remote = await ConnectAsync(host, port, ct);
            if (remote is null) { await Reply(s, "502 Bad Gateway", "", ct); return; }
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
            await PipeAsync(s, remote.GetStream(), ReadOnlyMemory<byte>.Empty, ct);
            return;
        }

        if (target.StartsWith('/'))
        {
            // запрос к самому прокси: PAC для настройки телефона, остальное — подсказка
            if (target.StartsWith("/proxy.pac", StringComparison.OrdinalIgnoreCase) || target.StartsWith("/wpad.dat", StringComparison.OrdinalIgnoreCase))
            {
                var hostHeader = lines.FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim();
                await Reply(s, "200 OK", Pac(hostHeader ?? $"127.0.0.1:{Port}", _o.Domains()), ct, "application/x-ns-proxy-autoconfig");
            }
            else await Reply(s, "200 OK", "Through home proxy. Wi-Fi → Proxy → Auto → http://<this computer>:" + Port + "/proxy.pac", ct);
            return;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme != "http") { await Reply(s, "400 Bad Request", "", ct); return; }
        using (var remote = await ConnectAsync(uri.Host, uri.Port, ct))
        {
            if (remote is null) { await Reply(s, "502 Bad Gateway", "", ct); return; }
            // обычный HTTP: запрос в виде, понятном серверу, без Proxy-* и с закрытием после ответа
            var sb = new StringBuilder($"{method} {uri.PathAndQuery} {parts[2]}\r\n");
            foreach (var l in lines.Skip(1).Where(l => l.Length > 0))
                if (!l.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                    sb.Append(l).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            await PipeAsync(s, remote.GetStream(), Encoding.ASCII.GetBytes(sb.ToString()), ct);
        }
    }

    private static bool EndsWithBlankLine(List<byte> h) =>
        h.Count >= 4 && h[^4] == '\r' && h[^3] == '\n' && h[^2] == '\r' && h[^1] == '\n';

    private static (string Host, int Port) SplitHostPort(string target, int fallback)
    {
        if (target.StartsWith('['))
        {
            var end = target.IndexOf(']');
            var p = end + 2 <= target.Length && target.Length > end + 1 && target[end + 1] == ':' ? int.Parse(target[(end + 2)..]) : fallback;
            return (target[1..end], p);
        }
        var colon = target.LastIndexOf(':');
        return colon > 0 && int.TryParse(target[(colon + 1)..], out var port) ? (target[..colon], port) : (target, fallback);
    }

    private static Task Reply(NetworkStream s, string status, string body, CancellationToken ct, string type = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        return s.WriteAsync(Encoding.ASCII.GetBytes(head).Concat(bytes).ToArray(), ct).AsTask();
    }

    /// <summary>PAC: домены обхода — через прокси (а если он недоступен — напрямую), остальное — напрямую.</summary>
    public static string Pac(string proxyHostPort, IReadOnlyList<string> domains)
    {
        var sb = new StringBuilder("function FindProxyForURL(url, host) {\n  host = host.toLowerCase();\n  var d = [");
        sb.Append(string.Join(",", domains.Select(d => "\"" + d.Replace("\"", "") + "\"")));
        sb.Append("];\n  for (var i = 0; i < d.length; i++)\n    if (host == d[i] || dnsDomainIs(host, \".\" + d[i]))\n");
        sb.Append($"      return \"PROXY {proxyHostPort}; DIRECT\";\n  return \"DIRECT\";\n}}\n");
        return sb.ToString();
    }

    private async Task<TcpClient?> ConnectAsync(string host, int port, CancellationToken ct)
    {
        if (port is < 1 or > 65535) return null;
        IPAddress[] addrs;
        try
        {
            addrs = IPAddress.TryParse(host, out var ip) ? [ip]
                : _o.Resolve is { } r ? await r(host, ct) : await System.Net.Dns.GetHostAddressesAsync(host, ct);
        }
        catch (SocketException) { return null; }
        foreach (var a in addrs.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6))
        {
            // никаких соединений в саму домашнюю сеть через прокси: это не туннель в чужую локалку
            if (_o.BlockPrivateTargets && HomeProxyOptions.IsPrivate(a)) continue;
            var c = new TcpClient(a.AddressFamily) { NoDelay = true };
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
                t.CancelAfter(_o.ConnectTimeout);
                await c.ConnectAsync(a, port, t.Token);
                return c;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                c.Dispose();
            }
        }
        return null;
    }

    private async Task PipeAsync(NetworkStream client, NetworkStream remote, ReadOnlyMemory<byte> prefix, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!prefix.IsEmpty)
        {
            await remote.WriteAsync(prefix, cts.Token);
            Interlocked.Add(ref _up, prefix.Length);
        }
        async Task Copy(Stream from, Stream to, bool up)
        {
            var buf = new byte[64 * 1024];
            int n;
            while ((n = await from.ReadAsync(buf, cts.Token)) > 0)
            {
                await to.WriteAsync(buf.AsMemory(0, n), cts.Token);
                if (up) Interlocked.Add(ref _up, n); else Interlocked.Add(ref _down, n);
            }
        }
        var a = Copy(client, remote, true);
        var b = Copy(remote, client, false);
        await Task.WhenAny(a, b);
        cts.Cancel();
        try { await Task.WhenAll(a, b); }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        _listener?.Stop();
        if (_loop is not null)
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { }
        _cts.Dispose();
    }
}
