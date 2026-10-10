using System.Security.Cryptography;

namespace Tropa.Core.Telegram;

/// <summary>
/// AES-256-CTR потоком: счётчик — все 16 байт IV big-endian, как в cryptography (Python) и в MTProto obfuscated.
/// Гамма считается пачками по 64 блока через ECB, чтобы не дёргать AES на каждые 16 байт.
/// </summary>
public sealed class AesCtr : IDisposable
{
    private const int Blocks = 64;
    private readonly Aes _aes;
    private readonly ICryptoTransform _ecb;
    private readonly byte[] _counter = new byte[16];
    private readonly byte[] _counters = new byte[16 * Blocks];
    private readonly byte[] _stream = new byte[16 * Blocks];
    private int _pos = 16 * Blocks;

    public AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        if (key.Length != 32) throw new ArgumentException("ключ AES-256 — 32 байта", nameof(key));
        if (iv.Length != 16) throw new ArgumentException("IV — 16 байт", nameof(iv));
        _aes = Aes.Create();
        _aes.Key = key.ToArray();
        _aes.Mode = CipherMode.ECB;
        _aes.Padding = PaddingMode.None;
        _ecb = _aes.CreateEncryptor();
        iv.CopyTo(_counter);
    }

    /// <summary>Зашифровать или расшифровать на месте (в CTR это одно и то же).</summary>
    public void Transform(Span<byte> data)
    {
        while (data.Length > 0)
        {
            if (_pos == _stream.Length) Refill();
            var n = Math.Min(data.Length, _stream.Length - _pos);
            Xor(data[..n], _stream.AsSpan(_pos, n));
            _pos += n;
            data = data[n..];
        }
    }

    public byte[] TransformCopy(ReadOnlySpan<byte> data)
    {
        var copy = data.ToArray();
        Transform(copy.AsSpan());
        return copy;
    }

    /// <summary>Промотать гамму, не шифруя (после 64-байтного init).</summary>
    public void Skip(int count)
    {
        while (count > 0)
        {
            if (_pos == _stream.Length) Refill();
            var n = Math.Min(count, _stream.Length - _pos);
            _pos += n;
            count -= n;
        }
    }

    private void Refill()
    {
        for (var b = 0; b < Blocks; b++)
        {
            _counter.CopyTo(_counters, b * 16);
            for (var i = 15; i >= 0 && ++_counter[i] == 0; i--) { }
        }
        _ecb.TransformBlock(_counters, 0, _counters.Length, _stream, 0);
        _pos = 0;
    }

    private static void Xor(Span<byte> data, ReadOnlySpan<byte> key)
    {
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var w = System.Numerics.Vector<byte>.Count;
            for (; i + w <= data.Length; i += w)
            {
                var v = new System.Numerics.Vector<byte>(data[i..]) ^ new System.Numerics.Vector<byte>(key[i..]);
                v.CopyTo(data[i..]);
            }
        }
        for (; i < data.Length; i++) data[i] ^= key[i];
    }

    public void Dispose()
    {
        _ecb.Dispose();
        _aes.Dispose();
    }
}
