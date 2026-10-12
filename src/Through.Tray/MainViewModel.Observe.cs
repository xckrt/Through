using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Through.Core.Control;

namespace Through.Tray;

/// <summary>Запущенная программа с окном — кандидат в «свои приложения».</summary>
public sealed record RunningAppVm(string Process, string Title, ImageSource? Icon);

/// <summary>
/// «Выбрать запущенную программу»: человек тычет в игру, служба полминуты слушает, куда та ходит
/// (DNS-имена и адреса с портами), и форма приложения заполняется сама. Остаётся проверить и нажать «Добавить».
/// </summary>
public sealed partial class MainViewModel
{
    private int _observeSeconds = 30;
    private bool _pickerVisible, _observing;
    private string _observeText = "";
    private DispatcherTimer? _observeTimer;

    public ObservableCollection<RunningAppVm> RunningApps { get; } = [];
    public bool PickerVisible { get => _pickerVisible; private set => Set(ref _pickerVisible, value); }
    public bool Observing { get => _observing; private set { Set(ref _observing, value); Raise(nameof(NotObserving)); CommandManager.InvalidateRequerySuggested(); } }
    public bool NotObserving => !_observing;
    public string ObserveText { get => _observeText; private set => Set(ref _observeText, Loc.Msg(value)); }

    /// <summary>Сколько секунд слушать выбранную программу (30 с … 5 минут): для онлайн-игр нужно дольше.</summary>
    public int ObserveSeconds { get => _observeSeconds; private set => Set(ref _observeSeconds, value); }

    public ICommand SetObserveTimeCommand => _setObserveTime ??= new ParamCommand(p =>
    {
        if (!Observing && p is string s && int.TryParse(s, out var sec)) ObserveSeconds = sec;
        return Task.CompletedTask;
    });
    private ICommand? _setObserveTime;

    public ICommand OpenPickerCommand => _openPicker ??= new RelayCommand(() =>
    {
        RunningApps.Clear();
        foreach (var app in WindowedApps()) RunningApps.Add(app);
        ObserveText = RunningApps.Count == 0
            ? Loc.T("Не вижу программ с окнами. Запустите игру или приложение и откройте список снова.")
            : Loc.T("Выберите программу и время. Сквозь послушает её, пока вы ей пользуетесь, и сама заполнит домены и порты. Для онлайн-игр берите больше времени — нужно успеть зайти в матч.");
        PickerVisible = true;
        FormVisible = false;
        return Task.CompletedTask;
    }, () => !Observing);
    private ICommand? _openPicker;

    public ICommand ClosePickerCommand => _closePicker ??= new RelayCommand(() =>
    {
        PickerVisible = false;
        return Task.CompletedTask;
    }, () => !Observing);
    private ICommand? _closePicker;

    public ICommand ObserveCommand => _observe ??= new ParamCommand(async p =>
    {
        if (p is not RunningAppVm app || Observing) return;
        Observing = true;
        var left = ObserveSeconds;
        ObserveText = Loc.F("Слушаю «{0}»: {1} с. Пользуйтесь программой как обычно — зайдите в матч, откройте чат.", app.Title, left);
        _observeTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) =>
        {
            if (--left > 0) ObserveText = Loc.F("Слушаю «{0}»: {1} с. Пользуйтесь программой как обычно — зайдите в матч, откройте чат.", app.Title, left);
            else ObserveText = Loc.T("Собираю, что услышал…");
        }, Application.Current.Dispatcher);
        try
        {
            await EnsureServiceAsync();
            var t = await _client!.CallAsync<ObservedTraffic>("apps.observe", new { process = app.Process, seconds = ObserveSeconds }, _cts.Token);
            var suggestion = AppSuggester.Suggest(app.Title, t);
            if (suggestion.Domains.Length == 0 && suggestion.UdpPorts.Length == 0 && suggestion.TcpPorts.Length == 0)
            {
                ObserveText = t.ProcessFound
                    ? Loc.F("За {0} с «{1}» никуда не ходил. Попробуйте ещё раз и поиграйте в это время: зайдите в матч или лобби.", t.Seconds, app.Title)
                    : Loc.F("«{0}» закрылся, пока я слушал. Запустите его и попробуйте снова.", app.Title);
                return;
            }
            PickerVisible = false;
            UseTemplateCommand.Execute(new AppTemplate(app.Title,
                Loc.F("Сквозь услышала {0} {1} и {2} ", t.Hosts.Length, Wording.Plural(t.Hosts.Length, "имя", "имени", "имён"), t.Endpoints.Length) +
                Loc.F("{0}. Лишнее можно удалить, недостающее — дописать.", Wording.Plural(t.Endpoints.Length, "адрес", "адреса", "адресов")),
                suggestion));
            Log(Loc.F("«{0}»: услышал {1} доменов и {2} портов", app.Title, suggestion.Domains.Length, suggestion.UdpPorts.Length + suggestion.TcpPorts.Length));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ObserveText = Loc.F("Не получилось послушать: {0}", ex.Message);
        }
        finally
        {
            _observeTimer?.Stop();
            Observing = false;
        }
    }, () => !Observing && !IsRunning);
    private ICommand? _observe;

    /// <summary>Программы с видимым окном, кроме самой Сквозь и оболочки Windows.</summary>
    private static List<RunningAppVm> WindowedApps()
    {
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "Through", "ApplicationFrameHost", "SystemSettings", "TextInputHost", "ShellExperienceHost",
            "StartMenuExperienceHost", "SearchHost", "LockApp",
        };
        var result = new List<RunningAppVm>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle) || skip.Contains(p.ProcessName)) continue;
                    if (result.Any(r => r.Process.Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase))) continue;
                    string title = p.ProcessName;
                    ImageSource? icon = null;
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (path is not null)
                        {
                            var info = FileVersionInfo.GetVersionInfo(path);
                            title = !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName!.Trim()
                                : !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription!.Trim() : title;
                            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                            if (ico is not null)
                            {
                                icon = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                                icon.Freeze();
                            }
                        }
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { }
                    result.Add(new RunningAppVm(p.ProcessName, title.Length > 40 ? title[..40] : title, icon));
                }
                catch (InvalidOperationException) { } // процесс завершился, пока смотрели
            }
        }
        return [.. result.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)];
    }
}
