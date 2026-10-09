using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Tropa.Setup;

/// <summary>
/// Tropa.Setup.exe — установка; /update — обновление без вопросов (его запускает окно Тропы);
/// /uninstall — удаление (его запускают «Приложения и возможности»).
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
        new SetupWindow(mode).Show();
    }
}
