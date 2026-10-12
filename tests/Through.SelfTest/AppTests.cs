using Through.Core.Control;

namespace Through.SelfTest;

/// <summary>Свои приложения: проверка ввода (он приходит от окна без прав) и путь через службу.</summary>
internal static class AppTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Приложения: адреса чистятся до доменов, порты и хост:порт принимаются", () =>
        {
            var a = UserApps.Normalize(new AppDefinition
            {
                Key = "DBD", Title = " Dead by Daylight ",
                Domains = ["https://Steam.Live.BhvrDBD.com/api/x", "*.eac-cdn.com", "bhvrdbd.com."],
                CheckHosts = ["steam.live.bhvrdbd.com:8443"],
                UdpPorts = ["7777", "27000-27100"],
            });
            Assert(a.Key == "dbd" && a.Title == "Dead by Daylight", $"{a.Key}/{a.Title}");
            Assert(a.Domains.SequenceEqual(["steam.live.bhvrdbd.com", "eac-cdn.com", "bhvrdbd.com"]), string.Join(",", a.Domains));
            Assert(a.CheckHosts.Single() == "steam.live.bhvrdbd.com:8443", a.CheckHosts.Single());
            return Task.CompletedTask;
        }),

        ("Приложения: попытки протащить аргументы winws и мусор отвергаются", () =>
        {
            AppDefinition Ok() => new() { Key = "x", Title = "X", Domains = ["x.com"] };
            (string Name, AppDefinition App)[] bad =
            [
                ("аргумент в домене", Ok() with { Domains = ["x.com --dpi-desync=fake"] }),
                ("--new в домене", Ok() with { Domains = ["--new"] }),
                ("путь в домене", Ok() with { Domains = [@"C:\Windows\x"] }),
                ("порт вне диапазона", Ok() with { UdpPorts = ["70000"] }),
                ("обратный диапазон", Ok() with { UdpPorts = ["200-100"] }),
                ("аргумент в порту", Ok() with { TcpPorts = ["443 --new"] }),
                ("ключ с пробелом", Ok() with { Key = "a b" }),
                ("пустое название", Ok() with { Title = "  " }),
                ("ни доменов, ни портов", Ok() with { Domains = [] }),
                ("плохой порт проверки", Ok() with { CheckHosts = ["x.com:0"] }),
            ];
            foreach (var (name, app) in bad)
            {
                try { UserApps.Normalize(app); }
                catch (InvalidDataException) { continue; }
                throw new Exception($"принято, хотя не должно: {name}");
            }
            return Task.CompletedTask;
        }),

        ("Приложения: игровые порты становятся профилем winws из шаблона службы", () =>
        {
            var app = UserApps.Normalize(new AppDefinition { Key = "game", Title = "Game", UdpPorts = ["7777"], TcpPorts = ["8000-8100"] });
            var presets = UserApps.Merge(new Through.Core.Tuning.PresetCatalog { Presets = [] }, [app]);
            var udp = presets.Companions.Single(c => c.Id == "game-udp");
            Assert(udp.Always && udp.Args.Contains("--filter-udp=7777") && udp.Args.Contains("--dpi-desync-any-protocol=1"),
                string.Join(" ", udp.Args));
            Assert(presets.Companions.Any(c => c.Id == "game-tcp" && c.Args.Contains("--filter-tcp=8000-8100")), "нет TCP-профиля");
            return Task.CompletedTask;
        }),
    ];

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
