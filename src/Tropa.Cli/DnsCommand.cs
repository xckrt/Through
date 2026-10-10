using System.Net;
using System.Net.Sockets;
using Tropa.Core.Doh;

/// <summary>--dns: поднять защищённый DNS на свободном порту и разрешить несколько имён через DoH. Систему не трогает.</summary>
internal static class DnsCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        await using var server = new SecureDnsServer(new SecureDnsOptions { Listen = [IPAddress.Loopback], Port = 0 });
        server.Start();
        Console.WriteLine($"DNS на 127.0.0.1:{server.Port}");
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var failed = 0;
        ushort id = 1;
        foreach (var name in new[] { "youtube.com", "discord.com", "rutracker.org", "web.telegram.org", "youtube.com" })
        {
            var started = DateTime.UtcNow;
            await udp.SendAsync(DnsMessage.Query(name, id: id++), new IPEndPoint(IPAddress.Loopback, server.Port), ct);
            var r = await udp.ReceiveAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(10), ct);
            var ok = DnsMessage.Rcode(r.Buffer.AsSpan()) == 0 && DnsMessage.AnswerCount(r.Buffer.AsSpan()) > 0;
            if (!ok) failed++;
            Console.WriteLine($"  {name,-18} {(ok ? "OK" : "нет")}  {(DateTime.UtcNow - started).TotalMilliseconds:0} мс");
        }
        var st = server.Stats();
        Console.WriteLine($"через {st.Upstream}, из кэша {st.CacheHits}{(st.LastError is { } e ? $", последняя ошибка: {e}" : "")}");
        return failed == 0 ? 0 : 1;
    }
}
