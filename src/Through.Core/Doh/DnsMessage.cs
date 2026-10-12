using System.Buffers.Binary;
using System.Text;

namespace Through.Core.Doh;

/// <summary>Минимум разбора DNS-сообщения (RFC 1035), нужный кэшу: ID, вопрос, наименьший TTL, SERVFAIL.</summary>
public static class DnsMessage
{
    public const int HeaderLength = 12;

    public static ushort Id(ReadOnlySpan<byte> m) => BinaryPrimitives.ReadUInt16BigEndian(m);

    public static void SetId(Span<byte> m, ushort id) => BinaryPrimitives.WriteUInt16BigEndian(m, id);

    /// <summary>Ключ кэша: первый вопрос (имя без учёта регистра + тип + класс). null — сообщение битое или без вопроса.</summary>
    public static string? QuestionKey(ReadOnlySpan<byte> m)
    {
        if (m.Length < HeaderLength || BinaryPrimitives.ReadUInt16BigEndian(m[4..]) == 0) return null;
        var at = HeaderLength;
        var name = new StringBuilder();
        while (true)
        {
            if (at >= m.Length) return null;
            int len = m[at++];
            if (len == 0) break;
            if (len > 63 || at + len > m.Length) return null; // в вопросе сжатия не бывает
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.ASCII.GetString(m.Slice(at, len)).ToLowerInvariant());
            at += len;
        }
        if (at + 4 > m.Length) return null;
        var type = BinaryPrimitives.ReadUInt16BigEndian(m[at..]);
        var cls = BinaryPrimitives.ReadUInt16BigEndian(m[(at + 2)..]);
        return $"{name}|{type}|{cls}";
    }

    /// <summary>Имя из первого вопроса — для журнала и тестов.</summary>
    public static string? QuestionName(ReadOnlySpan<byte> m) => QuestionKey(m)?.Split('|')[0];

    /// <summary>Наименьший TTL среди ответов и авторитетных записей; null — записей нет или сообщение битое.</summary>
    public static uint? MinTtl(ReadOnlySpan<byte> m)
    {
        try
        {
            if (m.Length < HeaderLength) return null;
            int qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
            int an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
            int ns = BinaryPrimitives.ReadUInt16BigEndian(m[8..]);
            var at = HeaderLength;
            for (var i = 0; i < qd; i++) at = SkipName(m, at) + 4;
            uint? min = null;
            for (var i = 0; i < an + ns; i++)
            {
                at = SkipName(m, at);
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(m[(at + 4)..]);
                var rdlen = BinaryPrimitives.ReadUInt16BigEndian(m[(at + 8)..]);
                at += 10 + rdlen;
                if (at > m.Length) return null;
                min = min is { } x ? Math.Min(x, ttl) : ttl;
            }
            return min;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static int Rcode(ReadOnlySpan<byte> m) => m.Length < 4 ? -1 : m[3] & 0x0F;

    private static int SkipName(ReadOnlySpan<byte> m, int at)
    {
        while (true)
        {
            var len = m[at];
            if (len == 0) return at + 1;
            if ((len & 0xC0) == 0xC0) return at + 2;
            at += len + 1;
        }
    }

    /// <summary>Ответ «сервер не смог» на запрос: заголовок и вопрос из запроса, флаги QR+RA, RCODE=2.</summary>
    public static byte[] ServFail(ReadOnlySpan<byte> query)
    {
        var end = HeaderLength;
        try
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(query[4..]) > 0) end = SkipName(query, HeaderLength) + 4;
        }
        catch (ArgumentOutOfRangeException) { end = HeaderLength; }
        end = Math.Min(end, query.Length);
        var r = query[..end].ToArray();
        if (r.Length < HeaderLength) Array.Resize(ref r, HeaderLength);
        r[2] = (byte)(0x80 | (r[2] & 0x01)); // QR, сохраняем RD
        r[3] = 0x80 | 2;                      // RA, SERVFAIL
        BinaryPrimitives.WriteUInt16BigEndian(r.AsSpan(4), (ushort)(end > HeaderLength ? 1 : 0));
        r.AsSpan(6, 6).Clear();
        return r;
    }

    /// <summary>Запрос A/AAAA для проверки резолвера.</summary>
    public static byte[] Query(string name, ushort type = 1, ushort id = 0)
    {
        var labels = name.TrimEnd('.').Split('.');
        var q = new List<byte>();
        q.AddRange([(byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var l in labels)
        {
            q.Add((byte)l.Length);
            q.AddRange(Encoding.ASCII.GetBytes(l));
        }
        q.AddRange([0, (byte)(type >> 8), (byte)type, 0, 1]);
        return [.. q];
    }

    /// <summary>Сколько записей в ответе (ANCOUNT).</summary>
    public static int AnswerCount(ReadOnlySpan<byte> m) => m.Length < HeaderLength ? 0 : BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
}
