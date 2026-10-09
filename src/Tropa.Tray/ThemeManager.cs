using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Tropa.Tray;

public enum ThemeChoice { System, Light, Dark }

/// <summary>
/// Тема окна: палитра — первый словарь в ресурсах приложения, здесь он подменяется на Light/Dark.
/// «Как в Windows» следует за параметром «Режим приложений» и переключается вместе с системой.
/// </summary>
public static class ThemeManager
{
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _subscribed;

    public static bool IsDark { get; private set; }

    /// <summary>Тема сменилась: то, что рисуется кодом (тропа), перерисовывается.</summary>
    public static event Action? Changed;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        if (!_subscribed)
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                // условие проверяется и в момент применения: за время в очереди человек мог выбрать тему сам
                if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (_choice == ThemeChoice.System) Apply(ThemeChoice.System);
                    });
            };
            _subscribed = true;
        }

        var dark = choice switch
        {
            ThemeChoice.Dark => true,
            ThemeChoice.Light => false,
            _ => SystemPrefersDark(),
        };
        // путь с именем сборки: относительный ищется в запущенной программе, а не в Тропе
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Tropa;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        // одна кисть на ключ на всё приложение, тема меняет только её цвет: так обновляются все элементы,
        // а подмена словаря по индексу до уже созданных элементов доходит не всегда
        var resources = Application.Current.Resources;
        foreach (var key in palette.Keys)
        {
            if (palette[key] is not SolidColorBrush themed) continue;
            if (resources.Contains(key) && resources[key] is SolidColorBrush { IsFrozen: false } live)
                live.Color = themed.Color;
            else
                resources[key] = new SolidColorBrush(themed.Color);
        }

        IsDark = dark;
        Changed?.Invoke();
    }

    /// <summary>Параметры → Персонализация → Цвета → «Режим приложений по умолчанию».</summary>
    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
