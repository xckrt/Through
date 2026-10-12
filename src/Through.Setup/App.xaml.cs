using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Through.Setup;

/// <summary>
/// Through.Setup.exe — установка; /update — обновление без вопросов (его запускает окно Сквозь);
/// /uninstall — удаление (его запускают «Приложения и возможности»); /silent — без окна (winget, скрипты):
/// ставит с ярлыком и автозапуском, код возврата 0 — успех, 1 — ошибка (подробности в install.log).
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args.Select(a => a.ToLowerInvariant()).ToArray();
        var mode = args.Contains("/uninstall") ? SetupMode.Uninstall
                 : args.Contains("/update") ? SetupMode.Update
                 : SetupMode.Install;

        // удалять Program Files, будучи запущенным оттуда, нельзя: продолжаем из копии во временной папке
        if (mode == SetupMode.Uninstall && SetupEngine.RelaunchFromTempForUninstall(e.Args))
        {
            Shutdown();
            return;
        }
        if (args.Contains("/silent") || args.Contains("/quiet") || args.Contains("/s"))
        {
            Shutdown(RunSilent(mode));
            return;
        }
        new SetupWindow(mode).Show();
    }

    private static int RunSilent(SetupMode mode)
    {
        var engine = new SetupEngine((_, _) => { });
        try
        {
            if (mode == SetupMode.Uninstall) engine.Uninstall(removeData: false);
            else engine.Install(new SetupOptions(DesktopShortcut: mode == SetupMode.Install, Autostart: true, LaunchAfter: false));
            return 0;
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"тихая установка: ошибка: {ex.Message}");
            return 1;
        }
    }
}
