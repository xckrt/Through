using Tropa.Core.Control;

namespace Tropa.SelfTest;

/// <summary>«Выбрать запущенную программу»: из подслушанного трафика — заготовка приложения.</summary>
internal static class ObserveTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Подслушанная программа: домены без шума Windows, частые первыми, порты диапазонами, 443/80 не в портах", () =>
        {
            var t = new ObservedTraffic
            {
                Process = "DeadByDaylight-Win64-Shipping",
                Hosts = ["steam.live.bhvrdbd.com", "steam.live.bhvrdbd.com", "cdn.live.dbd.bhvronline.com", "settings-win.data.microsoft.com", "ctldl.windowsupdate.com"],
                Endpoints =
                [
                    new("1.2.3.4", 443, "tcp", "gamelift.eu-central-1.amazonaws.com"),
                    new("5.6.7.8", 7777, "udp", null),
                    new("5.6.7.9", 7779, "udp", null),
                    new("5.6.7.10", 7800, "udp", null),
                    new("9.9.9.9", 8443, "tcp", null),
                ],
                ProcessFound = true,
            };
            var app = AppSuggester.Suggest("Dead by Daylight", t);
            Assert(app.Domains[0] == "bhvrdbd.com" && app.Domains.Contains("bhvronline.com") && app.Domains.Contains("amazonaws.com"), string.Join(",", app.Domains));
            Assert(!app.Domains.Any(d => d.Contains("microsoft") || d.Contains("windowsupdate")), "шум Windows не нужен");
            Assert(app.UdpPorts.SequenceEqual(["7777-7779", "7800"]) && app.TcpPorts.SequenceEqual(["8443"]),
                $"udp {string.Join(",", app.UdpPorts)} tcp {string.Join(",", app.TcpPorts)}");
            Assert(app.Always && app.Key == "dead-by-daylight" && app.CheckHosts[0] == "steam.live.bhvrdbd.com", $"{app.Key} {app.Always}");
            UserApps.Normalize(app); // заготовка проходит ту же проверку, что и ручной ввод
            return Task.CompletedTask;
        }),

        ("Подслушать: принимается только имя процесса, пути и мусор — нет", () =>
        {
            Assert(AppSuggester.NormalizeProcess("Discord.exe") == "Discord", "exe отрезается");
            foreach (var bad in new[] { @"C:\x\a.exe", "a;rm", "", new string('a', 80), "../x" })
            {
                try
                {
                    AppSuggester.NormalizeProcess(bad);
                    throw new Exception($"принял «{bad}»");
                }
                catch (InvalidDataException) { }
            }
            return Task.CompletedTask;
        }),
    ];

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
