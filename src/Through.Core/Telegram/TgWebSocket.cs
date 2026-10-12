using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Through.Core.Telegram;

/// <summary>Сервер ответил на upgrade не 101.</summary>
public sealed class WsHandshakeException(int status, string line) : IOException($"HTTP {status}: {line}")
{
    public int Status => status;
}

/// <summary>Куда и как открыть WebSocket: адрес для TCP, Host, имя в SNI и нужна ли проверка сертификата.</summary>
public sealed record WsEndpoint(string Address, int Port, string Host, string Sni, bool ValidateCertificate, string Path = "/apiws");

/// <summary>
/// Минимальный клиент WebSocket поверх TLS (RFC 6455, только binary): ровно то, что нужно для /apiws.
/// Свой, а не ClientWebSocket: нужен TCP к одному адресу, а Host и SNI — другие (вход через IP-перенаправление
/// и фронтинг), и кадры должны уходить по одному пакету MTProto без склейки.
/// </summary>
public sealed class TgWebSocket : IAsyncDisposable
{
    public const int MaxMessage = 16 * 1024 * 1024;
    private static readonly TimeSpan SynTimeout = TimeSpan.FromSeconds(1);
    private readonly TcpClient _tcp;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly byte[] _buf = new byte[64 * 1024];
    private int _pos, _len;
    private volatile bool _closed;

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    /// <summary>Закрыт нами, сервером (кадр close) или TCP оборвался: сокет читаем, а данных нет — это FIN.</summary>
    public bool IsClosed
    {
        get
        {
            if (_closed || !_tcp.Connected) return true;
            try { return _pos == _len && _tcp.Client.Poll(0, SelectMode.SelectRead) && _tcp.Client.Available == 0; }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return true; }
        }
    }

    private TgWebSocket(TcpClient tcp, Stream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<TgWebSocket> ConnectAsync(WsEndpoint ep, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        TcpClient? tcp = null;
        TgWebSocket? ws = null;
        var stage = "TCP";
        try
        {
            tcp = await ConnectTcpAsync(ep.Address, ep.Port, cts.Token);
            stage = "TLS";
            var ssl = new SslStream(tcp.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = ep.Sni,
                // фронтинг: в SNI чужое имя, сертификат заведомо не совпадёт; данные всё равно зашифрованы MTProto
                RemoteCertificateValidationCallback = ep.ValidateCertificate ? null : (_, _, _, _) => true,
            }, cts.Token);
            ws = new TgWebSocket(tcp, ssl);
            stage = "upgrade";
            await ws.HandshakeAsync(ep, cts.Token);
            return ws;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DisposeQuietly();
            throw new TimeoutException($"WebSocket {ep.Host} через {ep.Address}: {stage} не ответил за {timeout.TotalSeconds:0} с");
        }
        catch
        {
            await DisposeQuietly();
            throw;
        }

        async Task DisposeQuietly()
        {
            if (ws is not null) await ws.DisposeAsync();
            else tcp?.Dispose();
        }
    }

    /// <summary>
    /// TCP с короткими попытками: на части сетей SYN к адресам Telegram пропадает целиком, и повторы ядра
    /// на том же порту не помогают, а новое соединение (другой порт источника) проходит сразу.
    /// </summary>
    public static async Task<TcpClient> ConnectTcpAsync(string address, int port, CancellationToken ct, int attempts = 6)
    {
        for (var i = 1; ; i++)
        {
            var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 256 * 1024, SendBufferSize = 256 * 1024 };
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(SynTimeout);
            try
            {
                await tcp.ConnectAsync(address, port, attempt.Token);
                return tcp;
            }
            catch (Exception ex) when (i < attempts && !ct.IsCancellationRequested && ex is OperationCanceledException or SocketException)
            {
                tcp.Dispose();
                if (ex is SocketException) await Task.Delay(200, ct); // отказ, а не тишина: не долбим подряд
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }
    }

    private async Task HandshakeAsync(WsEndpoint ep, CancellationToken ct)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var request = $"GET {ep.Path} HTTP/1.1\r\nHost: {ep.Host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                      $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: binary\r\n\r\n";
        await _stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        await _stream.FlushAsync(ct);

        var status = await ReadLineAsync(ct) ?? throw new WsHandshakeException(0, "пустой ответ");
        while (await ReadLineAsync(ct) is { Length: > 0 }) { } // заголовки не нужны
        var parts = status.Split(' ', 3);
        var code = parts.Length >= 2 && int.TryParse(parts[1], out var c) ? c : 0;
        if (code != 101) throw new WsHandshakeException(code, status);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_pos == _len && !await FillAsync(ct)) return line.Length == 0 ? null : line.ToString();
            var b = _buf[_pos++];
            if (b == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length > 8192) throw new IOException("слишком длинная строка в ответе");
            line.Append((char)b);
        }
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _len = await _stream.ReadAsync(_buf, ct);
        _pos = 0;
        return _len > 0;
    }

    private async Task ReadExactlyAsync(Memory<byte> dst, CancellationToken ct)
    {
        while (dst.Length > 0)
        {
            if (_pos == _len && !await FillAsync(ct)) throw new EndOfStreamException("WebSocket закрыт посреди кадра");
            var n = Math.Min(dst.Length, _len - _pos);
            _buf.AsMemory(_pos, n).CopyTo(dst);
            _pos += n;
            dst = dst[n..];
        }
    }

    public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => SendFramesAsync([data], 0x2, ct);

    /// <summary>Несколько кадров одной записью: пакеты MTProto из одного куска потока.</summary>
    public Task SendAsync(IReadOnlyList<byte[]> parts, CancellationToken ct) =>
        SendFramesAsync(parts.Select(p => (ReadOnlyMemory<byte>)p).ToArray(), 0x2, ct);

    private async Task SendFramesAsync(ReadOnlyMemory<byte>[] payloads, byte opcode, CancellationToken ct)
    {
        if (_closed && opcode != 0x8) throw new IOException("WebSocket закрыт");
        var total = payloads.Sum(p => p.Length + 14);
        var frame = new byte[total];
        var at = 0;
        foreach (var p in payloads) at += WriteFrame(frame.AsSpan(at), opcode, p.Span);
        await _write.WaitAsync(ct);
        try
        {
            await _stream.WriteAsync(frame.AsMemory(0, at), ct);
            await _stream.FlushAsync(ct);
        }
        finally { _write.Release(); }
    }

    // клиентские кадры обязаны быть замаскированы
    private static int WriteFrame(Span<byte> dst, byte opcode, ReadOnlySpan<byte> payload)
    {
        dst[0] = (byte)(0x80 | opcode);
        int at;
        if (payload.Length < 126)
        {
            dst[1] = (byte)(0x80 | payload.Length);
            at = 2;
        }
        else if (payload.Length < 65536)
        {
            dst[1] = 0x80 | 126;
            BinaryPrimitives.WriteUInt16BigEndian(dst[2..], (ushort)payload.Length);
            at = 4;
        }
        else
        {
            dst[1] = 0x80 | 127;
            BinaryPrimitives.WriteUInt64BigEndian(dst[2..], (ulong)payload.Length);
            at = 10;
        }
        Span<byte> mask = dst.Slice(at, 4);
        RandomNumberGenerator.Fill(mask);
        at += 4;
        for (var i = 0; i < payload.Length; i++) dst[at + i] = (byte)(payload[i] ^ mask[i & 3]);
        return at + payload.Length;
    }

    /// <summary>Следующее сообщение целиком. null — сервер закрыл соединение.</summary>
    public async Task<byte[]?> ReceiveAsync(CancellationToken ct)
    {
        MemoryStream? fragments = null;
        var header = new byte[8];
        while (!_closed)
        {
            await ReadExactlyAsync(header.AsMemory(0, 2), ct);
            var fin = (header[0] & 0x80) != 0;
            var opcode = header[0] & 0x0F;
            var masked = (header[1] & 0x80) != 0;
            long length = header[1] & 0x7F;
            if (length == 126)
            {
                await ReadExactlyAsync(header.AsMemory(0, 2), ct);
                length = BinaryPrimitives.ReadUInt16BigEndian(header);
            }
            else if (length == 127)
            {
                await ReadExactlyAsync(header.AsMemory(0, 8), ct);
                length = (long)BinaryPrimitives.ReadUInt64BigEndian(header);
            }
            if (length is < 0 or > MaxMessage) throw new IOException($"кадр WebSocket слишком большой: {length} байт");
            var mask = new byte[4];
            if (masked) await ReadExactlyAsync(mask, ct);
            var payload = new byte[length];
            await ReadExactlyAsync(payload, ct);
            if (masked) for (var i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

            switch (opcode)
            {
                case 0x8: // close
                    _closed = true;
                    try { await SendFramesAsync([payload.AsMemory(0, Math.Min(2, payload.Length))], 0x8, ct); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                    return null;
                case 0x9: // ping
                    await SendFramesAsync([payload], 0xA, ct);
                    continue;
                case 0xA:
                    continue;
                case 0x0 or 0x1 or 0x2:
                    if (fin && fragments is null) return payload;
                    fragments ??= new MemoryStream();
                    fragments.Write(payload);
                    if (fragments.Length > MaxMessage) throw new IOException("сообщение WebSocket слишком большое");
                    if (fin) return fragments.ToArray();
                    continue;
                default:
                    continue;
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            _closed = true;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                var frame = new byte[6];
                var n = WriteFrame(frame, 0x8, []);
                await _write.WaitAsync(cts.Token);
                try { await _stream.WriteAsync(frame.AsMemory(0, n), cts.Token); }
                finally { _write.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        }
        try { await _stream.DisposeAsync(); } catch (IOException) { }
        _tcp.Dispose();
    }
}
