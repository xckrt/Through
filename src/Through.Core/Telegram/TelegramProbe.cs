using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Through.Core.Telegram;

public sealed record TelegramDcCheck(int Dc, bool Ok, int ElapsedMs, string? Error);

/// <summary>
/// Проверка прокси глазами клиента Telegram: подключиться с секретом, попросить у DC req_pq_multi
/// (первый шаг авторизации, ответ без ключей и аккаунта) и дождаться resPQ с нашим nonce.
/// Пришёл — до DC доходит всё: init, перешифровка, WebSocket и обратный путь.
/// </summary>
public static class TelegramProbe
{
    private const uint ReqPqMulti = 0xbe7e8ef1, ResPq = 0x05162463;

    /// <param name="self">Получает локальный порт до подключения: так прокси узнаёт свою проверку и не считает её за Telegram.</param>
    public static async Task<TelegramDcCheck> CheckAsync(string host, int port, byte[] secret, int dc, TimeSpan timeout,
        CancellationToken ct = default, Action<int>? self = null)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            using var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            tcp.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            self?.Invoke(((IPEndPoint)tcp.Client.LocalEndPoint!).Port);
            await tcp.ConnectAsync(host, port, cts.Token);
            var s = tcp.GetStream();

            var init = MtProto.BuildInit(MtProtoTransport.Intermediate, (short)dc, secret);
            var (enc, dec) = MtProto.InitiatorCiphers(init, secret);
            using (enc) using (dec)
            {
                var nonce = RandomNumberGenerator.GetBytes(16);
                var packet = new byte[4 + 40];
                BinaryPrimitives.WriteInt32LittleEndian(packet, 40);
                // auth_key_id = 0, message_id ≈ unixtime·2³², длина тела, req_pq_multi#be7e8ef1 nonce:int128
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(12), DateTimeOffset.UtcNow.ToUnixTimeSeconds() << 32);
                BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20), 20);
                BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24), ReqPqMulti);
                nonce.CopyTo(packet, 28);
                enc.Transform(packet.AsSpan());

                await s.WriteAsync(init, cts.Token);
                await s.WriteAsync(packet, cts.Token);

                var len = new byte[4];
                await s.ReadExactlyAsync(len, cts.Token);
                dec.Transform(len.AsSpan());
                var n = BinaryPrimitives.ReadInt32LittleEndian(len) & 0x7FFFFFFF;
                if (n is < 40 or > 4096) return new(dc, false, Ms(sw), $"DC{dc} прислал непонятный ответ ({n} байт)");
                var body = new byte[n];
                await s.ReadExactlyAsync(body, cts.Token);
                dec.Transform(body.AsSpan());

                var ok = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(20)) == ResPq && body.AsSpan(24, 16).SequenceEqual(nonce);
                return new(dc, ok, Ms(sw), ok ? null : $"DC{dc} ответил не resPQ");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(dc, false, Ms(sw), $"DC{dc}: нет ответа за {timeout.TotalSeconds:0} с");
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException)
        {
            return new(dc, false, Ms(sw), $"DC{dc}: {ex.Message}");
        }
    }

    private static int Ms(Stopwatch sw) => (int)sw.ElapsedMilliseconds;
}
