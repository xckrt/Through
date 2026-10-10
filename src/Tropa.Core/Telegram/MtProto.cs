using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tropa.Core.Telegram;

/// <summary>Вид транспорта MTProto: первые четыре байта после расшифровки init (позиция 56).</summary>
public enum MtProtoTransport : uint
{
    Abridged = 0xEFEFEFEF,
    Intermediate = 0xEEEEEEEE,
    PaddedIntermediate = 0xDDDDDDDD,
}

/// <summary>Что клиент Telegram сказал в своём init: в какой DC идёт и каким транспортом.</summary>
public sealed record ClientInit(int Dc, bool IsMedia, bool IsTest, MtProtoTransport Transport, byte[] Raw)
{
    /// <summary>Номер DC со знаком для init к Telegram: минус — медиа. Тестовый DC идёт своим путём, номер без +10000.</summary>
    public short DcIndex => (short)(IsMedia ? -Dc : Dc);
}

/// <summary>
/// MTProto obfuscated2: 64-байтный init, из которого обе стороны выводят ключи AES-CTR.
/// Порт логики tg-ws-proxy (Flowseal, MIT). С секретом прокси ключ = SHA256(prekey + secret),
/// без секрета (так мы говорим с самим Telegram) prekey и есть ключ.
/// </summary>
public static class MtProto
{
    public const int InitLength = 64;
    private const int Skip = 8, PrekeyLength = 32, IvLength = 16, TagPos = 56, DcPos = 60;

    /// <summary>Ключи с точки зрения того, кто послал init: чем он шифрует отправку и чем расшифровывает ответ.</summary>
    public readonly record struct Keys(byte[] SendKey, byte[] SendIv, byte[] RecvKey, byte[] RecvIv);

    public static Keys DeriveKeys(ReadOnlySpan<byte> init, ReadOnlySpan<byte> secret)
    {
        var prekeyIv = init.Slice(Skip, PrekeyLength + IvLength).ToArray();
        var reversed = prekeyIv.Reverse().ToArray();
        return new Keys(
            Key(prekeyIv.AsSpan(0, PrekeyLength), secret), prekeyIv[PrekeyLength..],
            Key(reversed.AsSpan(0, PrekeyLength), secret), reversed[PrekeyLength..]);
    }

    private static byte[] Key(ReadOnlySpan<byte> prekey, ReadOnlySpan<byte> secret)
    {
        if (secret.IsEmpty) return prekey.ToArray();
        Span<byte> buf = stackalloc byte[PrekeyLength + secret.Length];
        prekey.CopyTo(buf);
        secret.CopyTo(buf[PrekeyLength..]);
        return SHA256.HashData(buf);
    }

    /// <summary>Разобрать init клиента. null — не тот секрет или не MTProto.</summary>
    public static ClientInit? TryParseClientInit(ReadOnlySpan<byte> init, ReadOnlySpan<byte> secret)
    {
        if (init.Length != InitLength) return null;
        var keys = DeriveKeys(init, secret);
        using var ctr = new AesCtr(keys.SendKey, keys.SendIv);
        var plain = ctr.TransformCopy(init);

        var tag = BinaryPrimitives.ReadUInt32LittleEndian(plain.AsSpan(TagPos));
        if (tag is not ((uint)MtProtoTransport.Abridged or (uint)MtProtoTransport.Intermediate or (uint)MtProtoTransport.PaddedIntermediate))
            return null;

        int dcIdx = BinaryPrimitives.ReadInt16LittleEndian(plain.AsSpan(DcPos));
        var dc = Math.Abs(dcIdx);
        var test = dc >= 10000;
        if (test) dc -= 10000;
        if (dc == 0) return null;
        return new ClientInit(dc, dcIdx < 0, test, (MtProtoTransport)tag, init.ToArray());
    }

    /// <summary>
    /// Собрать init от имени того, кто начинает соединение: случайные 64 байта, в хвосте транспорт и DC.
    /// secret пустой — init к самому Telegram (так прокси открывает канал в DC).
    /// </summary>
    public static byte[] BuildInit(MtProtoTransport transport, short dcIndex, ReadOnlySpan<byte> secret = default)
    {
        var rnd = new byte[InitLength];
        do RandomNumberGenerator.Fill(rnd);
        while (IsReserved(rnd));

        var keys = DeriveKeys(rnd, secret);
        using var ctr = new AesCtr(keys.SendKey, keys.SendIv);
        var encrypted = ctr.TransformCopy(rnd);

        Span<byte> tail = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, (uint)transport);
        BinaryPrimitives.WriteInt16LittleEndian(tail[4..], dcIndex);
        RandomNumberGenerator.Fill(tail[6..]);
        // хвост шифруется той же гаммой, что и весь init: гамма = encrypted ^ rnd
        for (var i = 0; i < 8; i++)
            rnd[TagPos + i] = (byte)(tail[i] ^ encrypted[TagPos + i] ^ rnd[TagPos + i]);
        return rnd;
    }

    // первые байты, которые сервер принял бы за другой протокол (HTTP, TLS, intermediate без обфускации)
    private static bool IsReserved(byte[] b)
    {
        if (b[0] == 0xEF) return true;
        var first = BinaryPrimitives.ReadUInt32BigEndian(b);
        if (first is 0x48454144 or 0x504F5354 or 0x47455420 or 0xEEEEEEEE or 0xDDDDDDDD or 0x16030102) return true;
        return b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0;
    }

    /// <summary>Шифры того, кто послал init: отправка (уже промотана за init) и приём.</summary>
    public static (AesCtr Send, AesCtr Recv) InitiatorCiphers(ReadOnlySpan<byte> init, ReadOnlySpan<byte> secret = default)
    {
        var k = DeriveKeys(init, secret);
        var send = new AesCtr(k.SendKey, k.SendIv);
        send.Skip(InitLength);
        return (send, new AesCtr(k.RecvKey, k.RecvIv));
    }

    /// <summary>Шифры того, кто init принял: расшифровка входящего (промотана за init) и шифрование ответа.</summary>
    public static (AesCtr Decrypt, AesCtr Encrypt) ResponderCiphers(ReadOnlySpan<byte> init, ReadOnlySpan<byte> secret = default)
    {
        var k = DeriveKeys(init, secret);
        var dec = new AesCtr(k.SendKey, k.SendIv);
        dec.Skip(InitLength);
        return (dec, new AesCtr(k.RecvKey, k.RecvIv));
    }

    /// <summary>Номер DC → IP по умолчанию (для запасного прямого TCP).</summary>
    public static readonly IReadOnlyDictionary<int, string> DcAddresses = new Dictionary<int, string>
    {
        [1] = "149.154.175.50",
        [2] = "149.154.167.51",
        [3] = "149.154.175.100",
        [4] = "149.154.167.91",
        [5] = "149.154.171.5",
        [203] = "91.105.192.100",
    };

    public static readonly IReadOnlyDictionary<int, string> TestDcAddresses = new Dictionary<int, string>
    {
        [1] = "149.154.175.10",
        [2] = "149.154.167.40",
        [3] = "149.154.175.117",
    };

    /// <summary>Имена WebSocket-входов Telegram Web для DC: у медиа сначала отдельный.</summary>
    public static string[] WsDomains(int dc, bool media)
    {
        if (dc == 203) dc = 2;
        return media ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"] : [$"kws{dc}.web.telegram.org"];
    }
}

/// <summary>
/// Режет поток клиента на пакеты транспорта MTProto, чтобы каждый ушёл отдельным кадром WebSocket:
/// Telegram Web ждёт пакет в кадре. Длину читает из расшифрованных байт, отдаёт перешифрованные.
/// Непонятный пакет (нулевая длина) выключает резку до конца соединения.
/// </summary>
public sealed class MsgSplitter(MtProtoTransport transport)
{
    private const int MaxPacket = 16 * 1024 * 1024;
    private byte[] _cipher = new byte[4096];
    private byte[] _plain = new byte[4096];
    private int _count;
    private bool _disabled;

    public List<byte[]> Split(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> plain)
    {
        var parts = new List<byte[]>();
        if (cipher.IsEmpty) return parts;
        if (_disabled)
        {
            parts.Add(cipher.ToArray());
            return parts;
        }
        if (_count + cipher.Length > _cipher.Length)
        {
            var size = Math.Max(_cipher.Length * 2, _count + cipher.Length);
            Array.Resize(ref _cipher, size);
            Array.Resize(ref _plain, size);
        }
        cipher.CopyTo(_cipher.AsSpan(_count));
        plain.CopyTo(_plain.AsSpan(_count));
        _count += cipher.Length;

        var offset = 0;
        while (offset < _count)
        {
            var len = NextLength(offset, _count - offset);
            if (len is null) break;
            if (len <= 0)
            {
                parts.Add(_cipher.AsSpan(offset, _count - offset).ToArray());
                offset = _count;
                _disabled = true;
                break;
            }
            parts.Add(_cipher.AsSpan(offset, len.Value).ToArray());
            offset += len.Value;
        }
        if (offset > 0)
        {
            _cipher.AsSpan(offset, _count - offset).CopyTo(_cipher);
            _plain.AsSpan(offset, _count - offset).CopyTo(_plain);
            _count -= offset;
        }
        return parts;
    }

    /// <summary>Остаток при закрытии клиента.</summary>
    public byte[]? Flush()
    {
        if (_count == 0) return null;
        var tail = _cipher.AsSpan(0, _count).ToArray();
        _count = 0;
        return tail;
    }

    private int? NextLength(int offset, int avail)
    {
        if (avail <= 0) return null;
        var p = _plain.AsSpan(offset, avail);
        if (transport == MtProtoTransport.Abridged)
        {
            int payload, header;
            if (p[0] is 0x7F or 0xFF)
            {
                if (avail < 4) return null;
                payload = (p[1] | p[2] << 8 | p[3] << 16) * 4;
                header = 4;
            }
            else
            {
                payload = (p[0] & 0x7F) * 4;
                header = 1;
            }
            if (payload <= 0 || payload > MaxPacket) return 0;
            return avail < header + payload ? null : header + payload;
        }

        if (avail < 4) return null;
        var length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(p) & 0x7FFFFFFF);
        if (length <= 0 || length > MaxPacket) return 0; // такой пакет не ждём целиком: режем без разбора
        return avail < 4L + length ? null : 4 + length;
    }
}
