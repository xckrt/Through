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

        case "network":
            // какую сеть видит служба: ключ профиля и имя
            Console.WriteLine(await WindowsNetworkIdentity.CurrentAsync(CancellationToken.None) is { } id ? $"{id.Name}  ({id.Key})" : "сеть не опознана");
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
