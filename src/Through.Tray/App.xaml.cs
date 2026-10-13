using System.Windows;
using System.Windows.Threading;

namespace Through.Tray;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // любое необработанное исключение сохраняется: при следующем запуске окно предложит отправить отчёт
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            if (a.ExceptionObject is Exception ex) CrashReports.Save(ex, Loc.T("приложение"));
        };
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            CrashReports.Save(a.Exception, Loc.T("фоновая задача"));
            a.SetObserved();
        };
        DispatcherUnhandledException += OnUiException;
        // язык — до первого окна: строки XAML переводятся при загрузке
        Loc.Use(UiSettings.Load().Language);
        // запуск вместе с Windows у ранней загрузки ПК: GPU и композиция DWM ещё не готовы, и окно,
        // показанное из трея, бывает полностью чёрным. Для автозапуска рисуем программно — это надёжно
        // убирает чёрный экран, а нашей скромной анимации CPU достаточно. Обычный запуск не трогаем.
        if (e.Args.Contains("--tray"))
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
    }

    /// <summary>Ошибка в окне: сохраняем, говорим человеку и продолжаем — защиту держит служба, окно можно не ронять.</summary>
    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReports.Save(e.Exception, Loc.T("окно"));
        e.Handled = true;
        MessageBox.Show(
            Loc.T("В окне Сквозь что-то пошло не так. Обход это не задело: его держит служба.\n\n") +
            Loc.T("Отчёт сохранён, при следующем запуске Сквозь предложит отправить его разработчику.\n\n") +
            e.Exception.Message,
            Loc.T("Сквозь"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
