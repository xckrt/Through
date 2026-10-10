using System.Windows;
using System.Windows.Threading;

namespace Tropa.Tray;

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
        base.OnStartup(e);
    }

    /// <summary>Ошибка в окне: сохраняем, говорим человеку и продолжаем — защиту держит служба, окно можно не ронять.</summary>
    private static void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReports.Save(e.Exception, Loc.T("окно"));
        e.Handled = true;
        MessageBox.Show(
            Loc.T("В окне Тропы что-то пошло не так. Обход это не задело: его держит служба.\n\n") +
            Loc.T("Отчёт сохранён, при следующем запуске Тропа предложит отправить его разработчику.\n\n") +
            e.Exception.Message,
            Loc.T("Тропа"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
