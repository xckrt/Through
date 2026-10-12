using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Through.Tray;

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

        // при каждой смене вкладки её содержимое проявляется: обзор — лесенкой, остальные — мягким входом
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.Page)) return;
            switch (_vm.Page)
            {
                case Pages.Overview: AnimateHome(); break;
                case Pages.Diagnostics: FadeIn(DiagnosticsPage); break;
                case Pages.Apps: FadeIn(AppsPage); break;
                case Pages.Telegram: FadeIn(TelegramPage); break;
                case Pages.Log: FadeIn(LogPage); break;
                case Pages.Settings: FadeIn(SettingsPage); break;
            }
        };

        // живой отклик: карточка приподнимается под курсором
        foreach (var card in new FrameworkElement[] { ZapretCard, TgCard, DnsCard, HomeCard })
        {
            var c = card;
            c.MouseEnter += (_, _) => Lift(c, -5);
            c.MouseLeave += (_, _) => Lift(c, 0);
        }

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
            _tray.Hint(Loc.T("Сквозь работает"), Loc.T("Окно свернулось в трей. Защиту держит служба, окно открывается значком у часов."));
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Заголовок, сцена и карточки проявляются лесенкой сверху вниз; сцена ещё и чуть «наезжает».
    /// Без анимаций в Windows — сразу на месте.</summary>
    private void AnimateHome()
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        // сцена-герой врывается с лёгким приближением, остальное — просто поднимается лесенкой
        Rise(HomeHead, 0);
        Rise(HomeStats, 70);
        Rise(HomeHero, 150, scaleFrom: 0.94);
        var delay = 230;
        foreach (var el in new FrameworkElement[] { HomeHintCard, ZapretCard, TgCard, DnsCard, HomeCard })
        {
            Rise(el, delay);
            delay += 85;
        }
    }

    /// <summary>Один элемент: проявляется и поднимается снизу, по желанию — с лёгким приближением.</summary>
    private static void Rise(FrameworkElement el, double beginMs, double scaleFrom = 1)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var begin = TimeSpan.FromMilliseconds(beginMs);
        var move = new TranslateTransform(0, 22);
        if (scaleFrom != 1)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(scaleFrom, scaleFrom);
            el.RenderTransform = new TransformGroup { Children = { scale, move } };
            var pop = new DoubleAnimation(scaleFrom, 1, TimeSpan.FromMilliseconds(600)) { BeginTime = begin, EasingFunction = ease };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
        else el.RenderTransform = move;
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(440)) { BeginTime = begin, EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(560)) { BeginTime = begin, EasingFunction = ease });
    }

    /// <summary>Мягкий вход страницы при переключении вкладки: проявление и лёгкий подъём.</summary>
    private static void FadeIn(FrameworkElement el)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var move = new TranslateTransform(0, 12);
        el.RenderTransform = move;
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
    }

    /// <summary>Карточка слегка приподнимается под курсором — живой отклик. Переиспользует тот же сдвиг, что и вход.</summary>
    private static void Lift(FrameworkElement el, double y)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        if (el.RenderTransform is not TranslateTransform t)
        {
            t = new TranslateTransform();
            el.RenderTransform = t;
        }
        t.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(y, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
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
        MaxButton.ToolTip = maximized ? Loc.T("Восстановить") : Loc.T("Развернуть");
    }
}
