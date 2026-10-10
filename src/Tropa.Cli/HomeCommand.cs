using System.Net;
using Tropa.Core.Proxy;

/// <summary>--home: поднять прокси для дома на 127.0.0.1 и сходить через него (HTTP и SOCKS5) на пару сайтов.</summary>
internal static class HomeCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        await using var proxy = new HomeProxy(new HomeProxyOptions { Port = 0, Listen = IPAddress.Loopback, Domains = () => ["youtube.com", "discord.com"] });
        proxy.Start();
        Console.WriteLine($"прокси на 127.0.0.1:{proxy.Port}");
        var failed = 0;
        foreach (var scheme in new[] { "http", "socks5" })
            foreach (var url in new[] { "https://www.youtube.com/generate_204", "https://discord.com/api/v9/gateway", $"http://127.0.0.1:{proxy.Port}/proxy.pac" })
            {
                if (url.Contains("proxy.pac") && scheme == "socks5") continue;
                using var http = new HttpClient(new SocketsHttpHandler
                {
                    Proxy = url.Contains("proxy.pac") ? null : new WebProxy($"{scheme}://127.0.0.1:{proxy.Port}"),
                    UseProxy = !url.Contains("proxy.pac"),
                }) { Timeout = TimeSpan.FromSeconds(15) };
                try
                {
                    using var r = await http.GetAsync(url, ct);
                    Console.WriteLine($"  {scheme,-7} {url,-48} HTTP {(int)r.StatusCode}");
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  {scheme,-7} {url,-48} ошибка: {ex.Message}");
                }
            }
        Console.WriteLine($"соединений {proxy.Stats().Total}, отправлено {proxy.Stats().BytesUp} Б, получено {proxy.Stats().BytesDown} Б");
        return failed == 0 ? 0 : 1;
    }
}
