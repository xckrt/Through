using System.Net;
using Through.Core.Control;
using Through.Core.Probing;

namespace Through.SelfTest;

/// <summary>«Почему не открывается?»: разбор ввода, домен для обхода, вердикт против «злых» серверов.</summary>
internal static class SiteCheckTests
{
    public static IEnumerable<(string, Func<Task>)> All(ProbeOptions opts) =>
    [
        ("Сайт: из вставленного адреса получается хост", () =>
        {
            Assert(SiteCheck.NormalizeHost("https://WWW.RuTracker.org/forum/index.php?x=1") == "www.rutracker.org", "ссылка");
            Assert(SiteCheck.NormalizeHost("  discord.com:443 ") == "discord.com", "порт");
            Assert(SiteCheck.NormalizeHost("кто.рф") == "xn--j1ail.xn--p1ai", $"кириллица: {SiteCheck.NormalizeHost("кто.рф")}");
            Assert(SiteCheck.NormalizeHost("не сайт") is null && SiteCheck.NormalizeHost("--new") is null, "мусор не должен проходить");
            return Task.CompletedTask;
        }),

        ("Сайт: в обход идёт регистрируемый домен", () =>
        {
            Assert(SiteCheck.RegistrableDomain("www.rutracker.org") == "rutracker.org", "rutracker");
            Assert(SiteCheck.RegistrableDomain("news.bbc.co.uk") == "bbc.co.uk", "co.uk");
            Assert(SiteCheck.RegistrableDomain("shop.site.com.ru") == "site.com.ru", "com.ru");
            Assert(SiteCheck.RegistrableDomain("x.com") == "x.com", "короткий");
            return Task.CompletedTask;
        }),

        ("Сайт: режут по имени → «обход поможет»; глушат адрес → «не поможет»; 403 → «отказывает сам сайт»", async () =>
        {
            var prober = new ServiceProber(opts);
            await using var sni = FakeServer.SniFilter("example.test", reset: true);
            await using var dead = FakeServer.ResetAfterClientHello();
            await using var geo = FakeServer.Https((s, ct) => FakeServer.WriteAsync(s, "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n", ct));

            async Task<SiteVerdict> At(FakeServer f) =>
                SiteCheck.Classify(await prober.ProbeAsync(SiteCheck.Target("example.test") with { Port = f.Port }));

            var v1 = await At(sni);
            var v2 = await At(dead);
            var v3 = await At(geo);
            Assert(v1 == SiteVerdict.Fixable, $"SNI-фильтр: {v1}");
            Assert(v2 == SiteVerdict.AddressBlocked, $"глухо на любой SNI: {v2}");
            Assert(v3 == SiteVerdict.SiteRefuses, $"403: {v3}");
        }),

        ("Сайт: подменённый DNS → «нужен другой DNS», а не обход", async () =>
        {
            var prober = new ServiceProber(opts with { Resolver = (_, _) => Task.FromResult(new[] { IPAddress.Any }), AllowPrivateAddresses = false });
            var r = await prober.ProbeAsync(SiteCheck.Target("blocked.test"));
            Assert(SiteCheck.Classify(r) == SiteVerdict.DnsSpoofed, $"{r.Failure}");
        }),
    ];

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
