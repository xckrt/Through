using System.Text;

namespace Tropa.Core.Telegram;

/// <summary>
/// QR-код (ISO/IEC 18004) для ссылки на прокси: байтовый режим, уровень коррекции M, версии 1–10,
/// маска с наименьшим штрафом. Своя реализация, чтобы не тащить пакет ради одной картинки.
/// </summary>
public static class QrCode
{
    // уровень M: (EC-кодовых слов на блок, блоков группы 1, данных в блоке 1, блоков группы 2, данных в блоке 2)
    private static readonly (int Ec, int B1, int D1, int B2, int D2)[] Blocks =
    [
        default,
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
    ];

    private static readonly int[][] Align =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34], [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    /// <summary>Матрица модулей: true — тёмный. Без поля тишины (его рисует вызывающий).</summary>
    public static bool[,] Encode(string text, int? forceMask = null)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var version = 1;
        for (; version <= 10; version++)
        {
            var b = Blocks[version];
            var capacity = b.B1 * b.D1 + b.B2 * b.D2;
            var bits = 4 + (version < 10 ? 8 : 16) + data.Length * 8;
            if (bits <= capacity * 8) break;
        }
        if (version > 10) throw new ArgumentException("слишком длинный текст для QR версии 10");
        var codewords = Interleave(version, DataCodewords(version, data));
        var size = version * 4 + 17;

        var fixedMap = new bool[size, size];
        var baseMatrix = new bool[size, size];
        DrawFunctionPatterns(version, baseMatrix, fixedMap);
        PlaceData(codewords, baseMatrix, fixedMap);

        bool[,]? best = null;
        var bestPenalty = int.MaxValue;
        foreach (var mask in forceMask is { } m ? [m] : Enumerable.Range(0, 8))
        {
            var mtx = (bool[,])baseMatrix.Clone();
            ApplyMask(mtx, fixedMap, mask);
            DrawFormat(mtx, mask);
            var p = Penalty(mtx);
            if (p < bestPenalty) { bestPenalty = p; best = mtx; }
        }
        return best!;
    }

    private static byte[] DataCodewords(int version, byte[] data)
    {
        var b = Blocks[version];
        var capacity = b.B1 * b.D1 + b.B2 * b.D2;
        var bits = new List<bool>();
        void Put(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add((value >> i & 1) == 1); }
        Put(0b0100, 4); // байтовый режим
        Put(data.Length, version < 10 ? 8 : 16);
        foreach (var x in data) Put(x, 8);
        Put(0, Math.Min(4, capacity * 8 - bits.Count)); // терминатор
        while (bits.Count % 8 != 0) bits.Add(false);
        var result = new List<byte>();
        for (var i = 0; i < bits.Count; i += 8)
        {
            var v = 0;
            for (var j = 0; j < 8; j++) v = v << 1 | (bits[i + j] ? 1 : 0);
            result.Add((byte)v);
        }
        for (var pad = 0; result.Count < capacity; pad++) result.Add(pad % 2 == 0 ? (byte)0xEC : (byte)0x11);
        return [.. result];
    }

    private static byte[] Interleave(int version, byte[] data)
    {
        var b = Blocks[version];
        var dataBlocks = new List<byte[]>();
        var at = 0;
        for (var i = 0; i < b.B1; i++) { dataBlocks.Add(data[at..(at + b.D1)]); at += b.D1; }
        for (var i = 0; i < b.B2; i++) { dataBlocks.Add(data[at..(at + b.D2)]); at += b.D2; }
        var ecBlocks = dataBlocks.Select(d => ReedSolomon(d, b.Ec)).ToList();

        var result = new List<byte>();
        for (var i = 0; i < Math.Max(b.D1, b.D2); i++)
            foreach (var d in dataBlocks) if (i < d.Length) result.Add(d[i]);
        for (var i = 0; i < b.Ec; i++)
            foreach (var e in ecBlocks) result.Add(e[i]);
        return [.. result];
    }

    // ---- GF(256), многочлен 0x11D
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte[] ReedSolomon(byte[] data, int ec)
    {
        // порождающий многочлен (x - a^0)(x - a^1)…(x - a^(ec-1)), старший коэффициент первым
        var gen = new byte[] { 1 };
        for (var i = 0; i < ec; i++)
        {
            var next = new byte[gen.Length + 1];
            for (var j = 0; j < gen.Length; j++)
            {
                next[j] ^= gen[j];
                next[j + 1] ^= Mul(gen[j], Exp[i]);
            }
            gen = next;
        }
        var rem = new byte[ec];
        foreach (var d in data)
        {
            var factor = (byte)(d ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ec - 1);
            rem[ec - 1] = 0;
            for (var j = 0; j < ec; j++) rem[j] ^= Mul(gen[j + 1], factor);
        }
        return rem;
    }

    // ---- служебные узоры

    private static void Set(bool[,] m, bool[,] f, int r, int c, bool dark)
    {
        m[r, c] = dark;
        f[r, c] = true;
    }

    private static void DrawFunctionPatterns(int version, bool[,] m, bool[,] f)
    {
        var size = m.GetLength(0);
        for (var i = 0; i < size; i++)
        {
            Set(m, f, 6, i, i % 2 == 0);
            Set(m, f, i, 6, i % 2 == 0);
        }
        foreach (var (r, c) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
            for (var dr = -1; dr <= 7; dr++)
                for (var dc = -1; dc <= 7; dc++)
                {
                    int rr = r + dr, cc = c + dc;
                    if (rr < 0 || cc < 0 || rr >= size || cc >= size) continue;
                    var ring = Math.Max(Math.Abs(dr - 3), Math.Abs(dc - 3));
                    Set(m, f, rr, cc, ring is not 2 and not 4 && dr is >= 0 and <= 6 && dc is >= 0 and <= 6);
                }
        var al = Align[version];
        foreach (var r in al)
            foreach (var c in al)
            {
                if ((r == 6 && c == 6) || (r == 6 && c == al[^1]) || (r == al[^1] && c == 6)) continue;
                for (var dr = -2; dr <= 2; dr++)
                    for (var dc = -2; dc <= 2; dc++)
                        Set(m, f, r + dr, c + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
        // зарезервировать место формата и тёмный модуль
        for (var i = 0; i < 9; i++)
        {
            f[8, i] = f[i, 8] = true;
        }
        for (var i = 0; i < 8; i++)
        {
            f[8, size - 1 - i] = f[size - 1 - i, 8] = true;
        }
        Set(m, f, size - 8, 8, true);

        if (version >= 7)
        {
            var bits = version << 12 | Bch(version, 0x1F25, 12);
            for (var i = 0; i < 18; i++)
            {
                var dark = (bits >> i & 1) == 1;
                int a = i / 3, b = i % 3 + size - 11;
                Set(m, f, a, b, dark);
                Set(m, f, b, a, dark);
            }
        }
    }

    private static int Bch(int value, int poly, int degree)
    {
        var v = value << degree;
        var top = 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)poly);
        while (v != 0 && 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v) >= top)
            v ^= poly << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v) - top);
        return v;
    }

    private static void DrawFormat(bool[,] m, int mask)
    {
        var size = m.GetLength(0);
        var data = 0b00 << 3 | mask; // уровень M = 00
        var bits = (data << 10 | Bch(data, 0x537, 10)) ^ 0x5412;
        bool Bit(int i) => (bits >> i & 1) == 1;
        // первая копия: столбец 8 сверху и строка 8 слева, вторая — строка 8 справа и столбец 8 снизу
        for (var i = 0; i <= 5; i++) m[i, 8] = Bit(i);
        m[7, 8] = Bit(6);
        m[8, 8] = Bit(7);
        m[8, 7] = Bit(8);
        for (var i = 9; i < 15; i++) m[8, 14 - i] = Bit(i);
        for (var i = 0; i < 8; i++) m[8, size - 1 - i] = Bit(i);
        for (var i = 8; i < 15; i++) m[size - 15 + i, 8] = Bit(i);
        m[size - 8, 8] = true;
    }

    private static void PlaceData(byte[] codewords, bool[,] m, bool[,] f)
    {
        var size = m.GetLength(0);
        var bit = 0;
        var total = codewords.Length * 8;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vert = 0; vert < size; vert++)
                for (var j = 0; j < 2; j++)
                {
                    var c = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var r = upward ? size - 1 - vert : vert;
                    if (f[r, c]) continue;
                    if (bit < total) m[r, c] = (codewords[bit >> 3] >> (7 - (bit & 7)) & 1) == 1;
                    bit++;
                }
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] f, int mask)
    {
        var size = m.GetLength(0);
        for (var r = 0; r < size; r++)
            for (var c = 0; c < size; c++)
            {
                if (f[r, c]) continue;
                var flip = mask switch
                {
                    0 => (r + c) % 2 == 0,
                    1 => r % 2 == 0,
                    2 => c % 3 == 0,
                    3 => (r + c) % 3 == 0,
                    4 => (r / 2 + c / 3) % 2 == 0,
                    5 => r * c % 2 + r * c % 3 == 0,
                    6 => (r * c % 2 + r * c % 3) % 2 == 0,
                    _ => ((r + c) % 2 + r * c % 3) % 2 == 0,
                };
                if (flip) m[r, c] = !m[r, c];
            }
    }

    private static int Penalty(bool[,] m)
    {
        var size = m.GetLength(0);
        var p = 0;
        // N1: пять и больше подряд одного цвета
        for (var pass = 0; pass < 2; pass++)
            for (var a = 0; a < size; a++)
            {
                var run = 1;
                for (var b = 1; b < size; b++)
                {
                    bool cur = pass == 0 ? m[a, b] : m[b, a], prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                    if (cur == prev) run++;
                    else { if (run >= 5) p += run - 2; run = 1; }
                }
                if (run >= 5) p += run - 2;
            }
        // N2: блоки 2×2
        for (var r = 0; r < size - 1; r++)
            for (var c = 0; c < size - 1; c++)
                if (m[r, c] == m[r + 1, c] && m[r, c] == m[r, c + 1] && m[r, c] == m[r + 1, c + 1]) p += 3;
        // N3: узор 1011101 с четырьмя светлыми с одной стороны
        bool[] pat1 = [true, false, true, true, true, false, true, false, false, false, false];
        bool[] pat2 = [false, false, false, false, true, false, true, true, true, false, true];
        for (var a = 0; a < size; a++)
            for (var b = 0; b + 11 <= size; b++)
                for (var pass = 0; pass < 2; pass++)
                {
                    bool Match(bool[] pat)
                    {
                        for (var k = 0; k < 11; k++)
                            if ((pass == 0 ? m[a, b + k] : m[b + k, a]) != pat[k]) return false;
                        return true;
                    }
                    if (Match(pat1)) p += 40;
                    if (Match(pat2)) p += 40;
                }
        // N4: доля тёмных
        var dark = 0;
        foreach (var x in m) if (x) dark++;
        var percent = dark * 100 / (size * size);
        p += Math.Abs(percent - 50) / 5 * 10;
        return p;
    }
}
