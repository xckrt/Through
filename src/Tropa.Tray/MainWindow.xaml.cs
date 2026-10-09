using System.ComponentModel;
using System.Windows;

namespace Tropa.Tray;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TrayIcon? _tray;
    private bool _exiting;

    public MainWindow() : this(new MainViewModel()) { }

    /// <param name="withTray">false — без значка у часов (снимки окна, тесты).</param>
    public MainWindow(MainViewModel vm, bool withTray = true)
    {
        _vm = vm;
        _vm.ApplySavedTheme();
        InitializeComponent();
        DataContext = _vm;

        if (withTray)
        {
            _tray = new TrayIcon();
            _tray.Open += ShowFromTray;
            _tray.ToggleProtection += () => _vm.ProtectCommand.Execute(null);
            _tray.Exit += () => { _exiting = true; Close(); };
            // уведомление у часов, только когда окно не на глазах
            _vm.Notice += (title, text) =>
            {
                if (_vm.Notify && (!IsVisible || !IsActive || WindowState == WindowState.Minimized))
                    _tray.Hint(title, text);
            };
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.Pill) or nameof(MainViewModel.PillText) or nameof(MainViewModel.ProtectLabel))
                    _tray.Update(_vm.Pill, _vm.PillText, _vm.ProtectingNow, _vm.HasService);
            };
        }

        // проверка запускается сама: открыл окно и сразу видишь картину
        Loaded += async (_, _) =>
        {
            if (Environment.GetCommandLineArgs().Contains("--tray") && _tray is not null) Hide();
            await _vm.StartAsync();
        };
        StateChanged += (_, _) => FitMaximized();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _vm.Cancel();
            _tray?.Dispose();
        };
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // защиту держит служба; закрытие окна её не трогает, поэтому по умолчанию прячемся в трей
        if (_exiting || _tray is null || !_vm.MinimizeToTray || !_vm.HasService) return;
        e.Cancel = true;
        Hide();
        if (_vm.FirstTrayHint())
            _tray.Hint("Тропа работает", "Окно свернулось в трей. Защиту держит служба, окно открывается значком у часов.");
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Minimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();

    /// <summary>Окно без системной рамки при разворачивании вылезает за экран на толщину рамки: компенсируем.</summary>
    private void FitMaximized()
    {
        var maximized = WindowState == WindowState.Maximized;
        Frame.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = maximized ? "" : "";
        MaxButton.ToolTip = maximized ? "Восстановить" : "Развернуть";
    }
}
