using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Through.Setup;

public enum SetupMode { Install, Update, Uninstall }

/// <summary>Шаг на тропе прогресса: пройден, текущий или впереди.</summary>
public sealed class StepVm(string label, int index, int count)
{
    public string Label => label;
    public Brush Dot { get; private set; } = Brushes.Transparent;
    public Brush Ring { get; private set; } = Brushes.Transparent;
    public Brush Text { get; private set; } = Brushes.Transparent;
    public Brush LeftLine { get; private set; } = Brushes.Transparent;
    public Brush RightLine { get; private set; } = Brushes.Transparent;
    public Visibility LeftVisible => index == 0 ? Visibility.Hidden : Visibility.Visible;
    public Visibility RightVisible => index == count - 1 ? Visibility.Hidden : Visibility.Visible;

    public void Set(int current)
    {
        Brush B(string key) => (Brush)Application.Current.FindResource(key);
        var done = index < current || current == count - 1;
        var active = index == current;
        Dot = done || active ? B("Pine") : B("Paper");
        Ring = done || active ? B("Pine") : B("Fog");
        Text = active ? B("Ink") : B("InkSoft");
        LeftLine = index <= current ? B("Pine") : B("Line");
        RightLine = index < current ? B("Pine") : B("Line");
    }
}

public partial class SetupWindow : Window
{
    private readonly SetupMode _mode;
    private readonly string[] _labels;
    private List<StepVm> _steps = [];
    private bool _busy;
    private bool _finished;

    public SetupWindow(SetupMode mode)
    {
        _mode = mode;
        InitializeComponent();
        VersionText.Text = SetupEngine.Version;
        FolderText.Text = $"Папка: {SetupEngine.InstallDir}";
        _labels = mode == SetupMode.Uninstall
            ? ["остановка", "ярлыки", "файлы", "настройки", "готово"]
            : ["остановка", "файлы", "служба", "ярлыки", "готово"];

        switch (mode)
        {
            case SetupMode.Install:
                Title = "Установка Сквозь";
                Headline.Text = SetupEngine.IsInstalled ? "Переустановка Сквозь" : "Установка Сквозь";
                Subline.Text = "Сквозь проверяет, как провайдер режет YouTube, Discord и другие сайты, и сама подбирает обход. " +
                               "Служба держит его после перезагрузки, окно живёт в трее.";
                break;
            case SetupMode.Update:
                Title = "Обновление Сквозь";
                Headline.Text = $"Обновляю Сквозь до {SetupEngine.Version}";
                Subline.Text = "Подобранные стратегии и свои приложения сохранятся.";
                // при обновлении настройки уже выбраны: ярлыки и автозапуск оставляем как были
                DesktopBox.IsChecked = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Сквозь.lnk"));
                AutostartBox.IsChecked = Microsoft.Win32.Registry.CurrentUser
                    .OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")?.GetValue("Through") is not null;
                Loaded += (_, _) => Start();
                break;
            case SetupMode.Uninstall:
                Title = "Удаление Сквозь";
                Headline.Text = "Удалить Сквозь?";
                Subline.Text = "Служба и winws остановятся, обход выключится. Сайты будут работать так, как их пропускает провайдер.";
                OptionsPanel.Visibility = Visibility.Collapsed;
                UninstallPanel.Visibility = Visibility.Visible;
                PrimaryButton.Content = "Удалить";
                PrimaryButton.Background = (Brush)FindResource("Signal");
                break;
        }
        if (mode != SetupMode.Uninstall && !SetupEngine.HasPayload)
            ShowError("В этом установщике нет файлов программы. Скачайте установщик со страницы релизов github.com/xckrt/Through.");
    }

    private void PrimaryClicked(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            if (_mode != SetupMode.Uninstall && LaunchBox.IsChecked == true && _launchPending) SetupEngine.LaunchThrough();
            Close();
            return;
        }
        Start();
    }

    private bool _launchPending;

    private async void Start()
    {
        if (_busy) return;
        _busy = true;
        OptionsPanel.Visibility = UninstallPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        PrimaryButton.IsEnabled = SecondaryButton.IsEnabled = CloseButton.IsEnabled = false;
        _steps = _labels.Select((l, i) => new StepVm(l, i, _labels.Length)).ToList();
        Show(0, _labels[0]);

        var options = new SetupOptions(DesktopBox.IsChecked == true, AutostartBox.IsChecked == true, LaunchAfter: false);
        var removeData = RemoveDataBox.IsChecked == true;
        var engine = new SetupEngine((i, text) => Dispatcher.Invoke(() => Show(i, text)));
        try
        {
            await Task.Run(() =>
            {
                if (_mode == SetupMode.Uninstall) engine.Uninstall(removeData);
                else engine.Install(options);
            });
            Finish();
        }
        catch (Exception ex)
        {
            SetupEngine.Log($"ошибка: {ex}");
            ShowError(ex.Message);
        }
        finally
        {
            _busy = false;
            SecondaryButton.IsEnabled = CloseButton.IsEnabled = true;
        }
    }

    private void Show(int index, string text)
    {
        foreach (var s in _steps) s.Set(index);
        Steps.ItemsSource = null;
        Steps.ItemsSource = _steps;
        StepText.Text = text;
    }

    private void Finish()
    {
        _finished = true;
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        switch (_mode)
        {
            case SetupMode.Uninstall:
                Headline.Text = "Сквозь удалена";
                Subline.Text = "Спасибо, что пользовались. Вернуться можно в любой момент: github.com/xckrt/Through.";
                PrimaryButton.Content = "Закрыть";
                PrimaryButton.Background = (Brush)FindResource("Pine");
                break;
            case SetupMode.Update:
                // при обновлении окно Сквозь закрылось ради установки: открываем его снова сами
                SetupEngine.LaunchThrough();
                Close();
                break;
            default:
                Headline.Text = "Сквозь установлена";
                Subline.Text = "Ярлык в меню «Пуск». В Сквозь нажмите «Подобрать обход»: остальное она сделает сама.";
                _launchPending = LaunchBox.IsChecked == true;
                PrimaryButton.Content = _launchPending ? "Открыть Сквозь" : "Готово";
                break;
        }
    }

    private void ShowError(string message)
    {
        Headline.Text = "Не получилось";
        Subline.Text = "Ничего страшного: можно запустить установщик ещё раз. Если повторится, пришлите журнал установки.";
        OptionsPanel.Visibility = UninstallPanel.Visibility = ProgressPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = message;
        LogButton.Visibility = Visibility.Visible;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Content = "Закрыть";
        SecondaryButton.IsEnabled = CloseButton.IsEnabled = true;
    }

    private void OpenLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(SetupEngine.LogPath))
            Process.Start(new ProcessStartInfo(SetupEngine.LogPath) { UseShellExecute = true })?.Dispose();
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }
}
