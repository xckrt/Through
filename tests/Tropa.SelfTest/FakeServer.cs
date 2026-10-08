using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Tropa.SelfTest;

/// <summary>Локальный сервер, который ведёт себя как DPI: рвёт, молчит, замораживает.</summary>
internal sealed class FakeServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private FakeServer(Func<Socket, CancellationToken, Task> handler)
    {
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                Socket s;
                try { s = await _listener.AcceptSocketAsync(_cts.Token); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    try { await handler(s, _cts.Token); }
                    catch { /* сервер-хулиган, ошибки ожидаемы */ }
                    finally { s.Dispose(); }
                });
            }
        });
    }

    /// <summary>Читает ClientHello и шлёт RST: так режут по SNI.</summary>
    public static FakeServer ResetAfterClientHello() => new(async (s, ct) =>
    {
        await s.ReceiveAsync(new byte[4096], ct);
        s.LingerState = new LingerOption(true, 0); // close() отправит RST вместо FIN
        s.Close();
    });

    /// <summary>Читает ClientHello и молчит: так дропают пакеты.</summary>
    public static FakeServer SilentAfterClientHello() => new(async (s, ct) =>
    {
        await s.ReceiveAsync(new byte[4096], ct);
        await Task.Delay(Timeout.Infinite, ct);
    });

    /// <summary>Нормальный TLS + HTTP, поведение ответа задаёт respond.</summary>
    public static FakeServer Https(Func<Stream, CancellationToken, Task> respond) => new(async (s, ct) =>
    {
        await using var ssl = new SslStream(new NetworkStream(s, ownsSocket: false));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Cert.Value }, ct);
        await ReadRequestAsync(ssl, ct);
        await respond(ssl, ct);
    });

    public static Task WriteAsync(Stream s, string text, CancellationToken ct) =>
        s.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    public static async Task WriteBodyAsync(Stream s, int bytes, CancellationToken ct)
    {
        var chunk = new byte[4096];
        for (var left = bytes; left > 0; left -= chunk.Length)
            await s.WriteAsync(chunk.AsMemory(0, Math.Min(left, chunk.Length)), ct);
        await s.FlushAsync(ct);
    }

    private static async Task ReadRequestAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[4096];
        var seen = new StringBuilder();
        while (!seen.ToString().Contains("\r\n\r\n"))
        {
            var n = await s.ReadAsync(buf, ct);
            if (n == 0) return;
            seen.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
    }

    private static readonly Lazy<X509Certificate2> Cert = new(() =>
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new X509Certificate2(cert.Export(X509ContentType.Pfx)); // реимпорт нужен SslStream на Linux
    });

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _loop;
    }
}
