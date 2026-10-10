using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
            _tray.ToggleProtection += () => _vm.ZapretToggleCommand.Execute(null);
            _tray.ToggleTelegram += () => _vm.TgToggleCommand.Execute(null);
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
                if (e.PropertyName is nameof(MainViewModel.TgOrb))
                    _tray.UpdateTelegram(_vm.TgOrb != OrbState.Off, true);
            };
        }

        // карточки обзора выезжают при каждом возвращении на него
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Page) && _vm.Page == Pages.Overview) AnimateHome();
        };

        // проверка запускается сама: открыл окно и сразу видишь картину
        Loaded += async (_, _) =>
        {
            AnimateHome();
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

    /// <summary>Заголовок и карточки поднимаются и проявляются лесенкой. Без анимаций в Windows — сразу на месте.</summary>
    private void AnimateHome()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var delay = 0;
        foreach (var el in new FrameworkElement[] { HomeHead, ZapretCard, TgCard })
        {
            var shift = new TranslateTransform(0, 18);
            el.RenderTransform = shift;
            var begin = TimeSpan.FromMilliseconds(delay);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            el.Opacity = 0;
            el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin, EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(520)) { BeginTime = begin, EasingFunction = ease });
            delay += 90;
        }
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
