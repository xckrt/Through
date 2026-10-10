using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using Tropa.Service;

// Tropa.Service install | uninstall | run
// Без аргументов exe запускает диспетчер служб Windows.
if (args.Length == 0)
{
    ServiceBase.Run(new TropaService());
    return 0;
}

Console.OutputEncoding = Encoding.UTF8;
try
{
    switch (args[0])
    {
        case "install":
            if (!Environment.IsPrivilegedProcess) return Fail("нужны права администратора");
            Installer.Install();
            Console.WriteLine($"служба установлена в {Installer.InstallDir} и запущена");
            return 0;

        case "uninstall":
            if (!Environment.IsPrivilegedProcess) return Fail("нужны права администратора");
            return Installer.Uninstall();

        case "observe":
            // отладка «добавить приложение»: Tropa.Service.exe observe Discord 20 (от администратора)
            if (!Environment.IsPrivilegedProcess) return Fail("нужны права администратора: ETW-сессия");
            {
                var t = await EtwAppObserver.ObserveAsync(Tropa.Core.Control.AppSuggester.NormalizeProcess(args[1]),
                    TimeSpan.FromSeconds(args.Length > 2 ? int.Parse(args[2]) : 20), CancellationToken.None);
                Console.WriteLine($"процесс найден: {t.ProcessFound}, имён {t.Hosts.Length}, адресов {t.Endpoints.Length}");
                foreach (var h in t.Hosts.Order()) Console.WriteLine($"  dns {h}");
                foreach (var e in t.Endpoints.OrderBy(e => e.Port)) Console.WriteLine($"  {e.Protocol} {e.Address}:{e.Port} {e.Host}");
                var app = Tropa.Core.Control.AppSuggester.Suggest(args[1], t);
                Console.WriteLine($"домены: {string.Join(", ", app.Domains)}");
                Console.WriteLine($"udp: {string.Join(", ", app.UdpPorts)}  tcp: {string.Join(", ", app.TcpPorts)}");
            }
            return 0;

        case "network":
            // какую сеть видит служба: ключ профиля и имя
            Console.WriteLine(await WindowsNetworkIdentity.CurrentAsync(CancellationToken.None) is { } id ? $"{id.Name}  ({id.Key})" : "сеть не опознана");
            return 0;

        case "portable":
            // переносная версия: окно запускает это с правами администратора; живёт, пока живо окно (args[1] — его PID)
            if (!Environment.IsPrivilegedProcess) return Fail("нужны права администратора: winws ставит драйвер WinDivert");
            if (System.ServiceProcess.ServiceController.GetServices().Any(s => s.ServiceName.Equals(Installer.Name, StringComparison.OrdinalIgnoreCase)))
                return Fail("Тропа уже установлена как служба: переносная версия рядом с ней не запускается");
            using (var cts = new CancellationTokenSource())
            {
                if (args.Length > 1 && int.TryParse(args[1], out var parent))
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var window = Process.GetProcessById(parent);
                            await window.WaitForExitAsync();
                        }
                        catch (ArgumentException) { } // окна уже нет
                        cts.Cancel();
                    });
                var data = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "data");
                await new ServiceHost(data).RunAsync(cts.Token);
            }
            return 0;

        case "run":
            // для отладки: та же служба в консоли, Ctrl+C останавливает и выключает winws
            if (!Environment.IsPrivilegedProcess) return Fail("нужны права администратора: winws ставит драйвер WinDivert");
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                Console.WriteLine("служба работает в консоли, Ctrl+C для остановки");
                await new ServiceHost().RunAsync(cts.Token);
            }
            return 0;

        default:
            return Fail("Tropa.Service install | uninstall | run");
    }
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                               or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
{
    return Fail(ex.Message);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    Installer.Log.Write("ошибка: " + message);
    return 1;
}

internal sealed class TropaService : ServiceBase
{
    private CancellationTokenSource? _cts;
    private Task? _run;

    public TropaService()
    {
        ServiceName = "Tropa";
        CanStop = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        _cts = new CancellationTokenSource();
        _run = Task.Run(() => new ServiceHost().RunAsync(_cts.Token));
        // упала сама: останавливаемся с ненулевым кодом, и диспетчер служб перезапустит по настройке failure
        _run.ContinueWith(t =>
        {
            if (!t.IsFaulted) return;
            ExitCode = 1;
            Stop();
        }, TaskScheduler.Default);
    }

    protected override void OnStop() => Shutdown();
    protected override void OnShutdown() => Shutdown();

    private void Shutdown()
    {
        RequestAdditionalTime(20_000);
        _cts?.Cancel();
        try { _run?.Wait(TimeSpan.FromSeconds(15)); }
        catch (AggregateException) { /* ошибка уже в журнале, служба всё равно останавливается */ }
    }
}
