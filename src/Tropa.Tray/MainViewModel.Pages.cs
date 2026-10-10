using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using Tropa.Core.Control;
using Tropa.Core.Probing;

namespace Tropa.Tray;

public enum Pages { Overview, Diagnostics, Apps, Telegram, Log, Settings }

/// <summary>Плашка в заголовке окна: что с защитой прямо сейчас.</summary>
public enum Pill { Off, On, Busy, Warn, NoService }

public sealed class ParamCommand(Func<object?, Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public async void Execute(object? parameter) => await run(parameter);
}

public sealed record LogLine(string Time, string Text, bool Error);

/// <summary>Строка на странице «Приложения»: встроенный сервис или своё приложение.</summary>
public sealed class AppRowVm(string key, string title, string details, bool isUser) : Observable
{
    private string _summary = "";
    private Health _health = Health.Pending;

    public string Key => key;
    public string Title => title;
    public string Details => details;
    public bool IsUser => isUser;
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public Health Health { get => _health; set => Set(ref _health, value); }
}

/// <summary>Заготовка приложения: домены собраны заранее, человек может их поправить.</summary>
public sealed record AppTemplate(string Name, string Note, AppDefinition App);

public sealed partial class MainViewModel
{
    private Pages _page = Pages.Overview;
    private string? _lastServiceMessage;
    private bool _syncingSettings;
    private readonly UiSettings _ui = UiSettings.Load();

    public Pages Page { get => _page; set => Set(ref _page, value); }

    public ICommand NavigateCommand => _navigate ??= new ParamCommand(p =>
    {
        if (p is string name && Enum.TryParse<Pages>(name, out var page)) Page = page;
        OnTelegramPage(Page == Pages.Telegram);
        if (Page == Pages.Apps) return RefreshAppsAsync();
        return Task.CompletedTask;
    });
    private ICommand? _navigate;

    // ---------- плашка состояния в заголовке

    private Pill _pill = Pill.NoService;
    private string _pillText = "служба не установлена";

    public Pill Pill { get => _pill; private set => Set(ref _pill, value); }
    public string PillText { get => _pillText; private set => Set(ref _pillText, value); }

    // ---------- журнал

    public ObservableCollection<LogLine> LogLines { get; } = [];

    private void Log(string text, bool error = false)
    {
        LogLines.Insert(0, new LogLine(DateTime.Now.ToString("HH:mm:ss"), text, error));
        while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
    }

    public ICommand OpenServiceLogCommand => _openLog ??= new RelayCommand(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tropa", "service.log");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        else Log("журнала службы ещё нет: служба не запускалась");
        return Task.CompletedTask;
    });
    private ICommand? _openLog;

    private string TitleOf(string key) => Services.FirstOrDefault(s => s.Key == key)?.Title ?? Wording.ServiceTitle(key);

    // ---------- приложения

    public ObservableCollection<AppRowVm> AppRows { get; } = [];
    private IReadOnlyList<AppDefinition> _userApps = [];

    public IReadOnlyList<AppTemplate> Templates { get; } =
    [
        new("Dead by Daylight", "Серверы игры на AWS и CloudFront, античит EasyAntiCheat на Cloudflare. Обход включается всегда: игра чаще тормозит, чем не открывается.",
            new AppDefinition
            {
                Key = "dbd", Title = "Dead by Daylight",
                Domains = ["bhvrdbd.com", "bhvronline.com", "easyanticheat.net", "eac-cdn.com", "epicgames.dev"],
                CheckHosts = ["steam.live.bhvrdbd.com", "cdn.live.dbd.bhvronline.com", "easyanticheat.net"],
                Always = true,
            }),
        new("Roblox", "Сайт и лаунчер. Если тормозит сама игра, добавьте её UDP-порты.",
            new AppDefinition
            {
                Key = "roblox", Title = "Roblox",
                Domains = ["roblox.com", "rbxcdn.com", "robloxlabs.com", "rbx.com"],
                CheckHosts = ["www.roblox.com", "apis.roblox.com"],
            }),
        new("Twitch", "Сайт, чат и видеопоток.",
            new AppDefinition
            {
                Key = "twitch", Title = "Twitch",
                Domains = ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net", "live-video.net"],
                CheckHosts = ["www.twitch.tv", "static-cdn.jtvnw.net"],
            }),
        new("Своё приложение", "Впишите домены, с которыми работает приложение. Поддомены обходятся сами.",
            new AppDefinition { Key = "", Title = "" }),
    ];

    private bool _formVisible;
    private string _formKey = "";
    private string _formTitle = "";
    private string _formDomains = "";
    private string _formCheck = "";
    private string _formUdp = "";
    private string _formTcp = "";
    private bool _formAlways;
    private string _formNote = "";
    private string? _formError;

    public bool FormVisible { get => _formVisible; set => Set(ref _formVisible, value); }
    public string FormTitle { get => _formTitle; set => Set(ref _formTitle, value); }
    public string FormDomains { get => _formDomains; set => Set(ref _formDomains, value); }
    public string FormCheck { get => _formCheck; set => Set(ref _formCheck, value); }
    public string FormUdp { get => _formUdp; set => Set(ref _formUdp, value); }
    public string FormTcp { get => _formTcp; set => Set(ref _formTcp, value); }
    public bool FormAlways { get => _formAlways; set => Set(ref _formAlways, value); }
    public string FormNote { get => _formNote; set => Set(ref _formNote, value); }
    public string? FormError { get => _formError; set { if (Set(ref _formError, value)) Raise(nameof(HasFormError)); } }
    public bool HasFormError => FormError is not null;

    public ICommand UseTemplateCommand => _useTemplate ??= new ParamCommand(p =>
    {
        if (p is not AppTemplate t) return Task.CompletedTask;
        _formKey = t.App.Key;
        FormTitle = t.App.Title;
        FormDomains = string.Join(Environment.NewLine, t.App.Domains);
        FormCheck = string.Join(", ", t.App.CheckHosts);
        FormUdp = string.Join(", ", t.App.UdpPorts);
        FormTcp = string.Join(", ", t.App.TcpPorts);
        FormAlways = t.App.Always;
        FormNote = t.Note;
        FormError = null;
        FormVisible = true;
        return Task.CompletedTask;
    });
    private ICommand? _useTemplate;

    public ICommand CancelFormCommand => _cancelForm ??= new RelayCommand(() =>
    {
        FormVisible = false;
        return Task.CompletedTask;
    });
    private ICommand? _cancelForm;

    public ICommand SubmitAppCommand => _submitApp ??= new RelayCommand(SubmitAppAsync, () => !IsRunning);
    private ICommand? _submitApp;

    public ICommand RemoveAppCommand => _removeApp ??= new ParamCommand(async p =>
    {
        if (p is not string key || _client is null) return;
        try
        {
            await _client.CallAsync<AppDefinition[]>("apps.remove", new { key }, _cts.Token);
            Log($"приложение «{TitleOf(key)}» удалено");
            Services.Remove(Services.First(s => s.Key == key));
            await RefreshAppsAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log(ex.Message, error: true);
        }
    }, () => !IsRunning && _client is not null);
    private ICommand? _removeApp;

    private async Task SubmitAppAsync()
    {
        static string[] Split(string s) => s.Split([',', ';', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);

        var key = _formKey.Length > 0 ? _formKey : Slug(FormTitle);
        AppDefinition app;
        try
        {
            // та же проверка, что в службе: ошибка видна сразу у поля, без похода в службу
            app = UserApps.Normalize(new AppDefinition
            {
                Key = key, Title = FormTitle,
                Domains = Split(FormDomains), CheckHosts = Split(FormCheck),
                UdpPorts = Split(FormUdp), TcpPorts = Split(FormTcp), Always = FormAlways,
            });
        }
        catch (InvalidDataException ex)
        {
            FormError = ex.Message;
            return;
        }

        FormError = null;
        try
        {
            IsRunning = true;
            await EnsureServiceAsync();
            await _client!.CallAsync<AppDefinition[]>("apps.add", app, _cts.Token);
            Log($"добавлено приложение «{app.Title}»");
            FormVisible = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            FormError = ex.Message;
            return;
        }
        finally
        {
            IsRunning = false;
        }

        await RefreshAppsAsync();
        // приложение с доменами сразу подбираем; одни игровые порты подбирать нечем, они включаются с защитой
        if (app.Domains.Length > 0) await TuneAsync([app.Key]);
        else await CheckAsync();
    }

    /// <summary>Ключ из названия: латиница и цифры, иначе app-xxxx.</summary>
    private static string Slug(string title)
    {
        var s = new string(title.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-').ToArray()).Trim('-');
        while (s.Contains("--")) s = s.Replace("--", "-");
        if (s.Length is 0 or > 32) s = "app-" + Math.Abs(title.GetHashCode() % 100000);
        return s;
    }

    private async Task RefreshAppsAsync()
    {
        if (_client is not null)
        {
            try { _userApps = await _client.CallAsync<AppDefinition[]>("apps", ct: _cts.Token); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException) { _userApps = []; }
        }

        var builtIn = LocalCatalog();
        AppRows.Clear();
        foreach (var (key, targets) in builtIn?.Services ?? [])
        {
            if (targets.All(t => t.IsControl)) continue;
            var domains = builtIn!.Domains.GetValueOrDefault(key, []).Count;
            AppRows.Add(Row(key, Wording.ServiceTitle(key), domains > 0 ? $"встроенный, доменов: {domains}" : "встроенный", false));
        }
        foreach (var a in _userApps)
        {
            var parts = new List<string> { $"доменов: {a.Domains.Length}" };
            if (a.UdpPorts.Length > 0) parts.Add($"UDP {string.Join(",", a.UdpPorts)}");
            if (a.TcpPorts.Length > 0) parts.Add($"TCP {string.Join(",", a.TcpPorts)}");
            if (a.Always) parts.Add("обход всегда");
            AppRows.Add(Row(a.Key, a.Title, string.Join(", ", parts), true));
        }
    }

    private AppRowVm Row(string key, string title, string details, bool user)
    {
        var row = new AppRowVm(key, title, details, user);
        if (Services.FirstOrDefault(s => s.Key == key) is { } svc)
        {
            row.Health = svc.Health;
            row.Summary = svc.Summary;
        }
        else
        {
            row.Summary = "ещё не проверялось";
        }
        return row;
    }

    private TargetCatalog? LocalCatalog()
    {
        try { return _catalog ??= TargetCatalog.Load(_targetsPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { return null; }
    }

    // ---------- настройки

    private bool _autoUpdate = true;
    private string _engineText = "zapret ещё не скачан";
    private string _updateText = "";
    private bool _autostart = Tray.Autostart.IsOn();

    public bool AutoUpdate
    {
        get => _autoUpdate;
        set
        {
            if (!Set(ref _autoUpdate, value) || _syncingSettings || _client is null) return;
            _ = CallQuietly("settings.autoUpdate", new { on = value });
        }
    }

    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (!Set(ref _autostart, value)) return;
            try { Tray.Autostart.Set(value); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log($"автозапуск: {ex.Message}", error: true); }
        }
    }

    /// <summary>Как в Windows / Светлая / Тёмная — для трёх кнопок в настройках.</summary>
    public string Theme
    {
        get => _ui.Theme.ToString();
        set
        {
            if (!Enum.TryParse<ThemeChoice>(value, out var t) || t == _ui.Theme) return;
            _ui.Theme = t;
            _ui.Save();
            ThemeManager.Apply(t);
            Raise(nameof(Theme));
        }
    }

    public ICommand SetThemeCommand => _setTheme ??= new ParamCommand(p =>
    {
        if (p is string t) Theme = t;
        return Task.CompletedTask;
    });
    private ICommand? _setTheme;

    /// <summary>Применить сохранённую тему при запуске окна.</summary>
    public void ApplySavedTheme() => ThemeManager.Apply(_ui.Theme);

    public bool MinimizeToTray
    {
        get => _ui.MinimizeToTray;
        set
        {
            if (_ui.MinimizeToTray == value) return;
            _ui.MinimizeToTray = value;
            _ui.Save();
            Raise(nameof(MinimizeToTray));
        }
    }

    public string EngineText { get => _engineText; private set => Set(ref _engineText, value); }
    public string UpdateText { get => _updateText; private set => Set(ref _updateText, value); }
    public string BuildText => $"Тропа, сборка {Core.TropaBuild.Id}";
    public bool HasService => _client is not null;
    public bool CanUninstall => HasService && !IsPortable;

    public ICommand CheckUpdatesCommand => _checkUpdates ??= new RelayCommand(async () =>
    {
        if (_client is null) return;
        IsRunning = true;
        UpdateText = "Проверяю…";
        try
        {
            var r = await _client.CallAsync<EngineUpdateResult>("engine.update", ct: _cts.Token);
            UpdateText = r.Message;
            Log(r.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            UpdateText = ex.Message;
            Log(ex.Message, error: true);
        }
        finally
        {
            IsRunning = false;
        }
    }, () => !IsRunning && _client is not null);
    private ICommand? _checkUpdates;

    public ICommand UninstallServiceCommand => _uninstall ??= new RelayCommand(async () =>
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Tropa.Service.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "uninstall") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is not null) await p.WaitForExitAsync();
            Log("служба Тропы удалена, winws остановлен");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log("удаление службы отменено");
        }
    }, () => !IsRunning && _client is not null);
    private ICommand? _uninstall;

    private async Task CallQuietly(string method, object data)
    {
        try { ApplyStatus(await _client!.CallAsync<StatusReport>(method, data, _cts.Token)); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { Log(ex.Message, error: true); }
    }

    /// <summary>Настройки и плашка из статуса службы.</summary>
    private void UpdateSettings(StatusReport s)
    {
        _syncingSettings = true;
        AutoUpdate = s.AutoUpdate;
        _syncingSettings = false;
        EngineText = s.EngineVersion is { } v ? $"zapret {v}" : "zapret скачается при первом подборе";
        if (s.LastUpdateCheck is { } at && UpdateText is "" or "Проверяю…")
            UpdateText = $"Последняя проверка обновлений: {at.LocalDateTime:dd.MM HH:mm}";
        Raise(nameof(HasService));
        Raise(nameof(CanUninstall));

        (Pill, PillText) = s switch
        {
            { State: ControllerState.Tuning } => (Pill.Busy, "подбираю обход"),
            { Protecting: true } when Services.Any(x => !x.IsControl && x.Health == Health.Blocked) => (Pill.Warn, "защита включена, не всё работает"),
            { Protecting: true } => (Pill.On, "защита включена"),
            { ProtectionWanted: true } => (Pill.Warn, "защита не запустилась"),
            _ => (Pill.Off, "защита выключена"),
        };
    }

    /// <summary>Служба сделала что-то сама или что-то сломалось: окно покажет уведомление у часов.</summary>
    public event Action<string, string>? Notice;

    /// <summary>Заголовок уведомления для сообщений службы, о которых стоит сказать, пока окно закрыто.</summary>
    private static string? Notable(string msg) =>
        msg.Contains("перестало работать") ? "Тропа подбирает обход заново"
        : msg.Contains("zapret обновлён") ? "zapret обновлён"
        : msg.Contains("остановился сам") ? "Тропа перезапустила движок"
        : msg.Contains("не удалось включить защиту") || msg.Contains("не запустился") ? "Тропа: нужна помощь"
        : msg.StartsWith("подбор закончен") ? "Подбор закончен"
        : msg.StartsWith("Telegram: не отвечают") || msg.StartsWith("прокси Telegram не запустился") ? "Telegram"
        : msg.StartsWith("порт ") && msg.Contains("прокси Telegram") ? "Telegram: подключите прокси заново"
        : null;

    public bool Notify
    {
        get => _ui.Notify;
        set
        {
            if (_ui.Notify == value) return;
            _ui.Notify = value;
            _ui.Save();
            Raise(nameof(Notify));
        }
    }

    /// <summary>Нажатие на значок в трее и закрытие окна смотрят сюда.</summary>
    public bool ProtectingNow => _protecting;

    /// <summary>Подсказку «окно в трее» показываем один раз.</summary>
    public bool FirstTrayHint()
    {
        if (_ui.TrayHintShown) return false;
        _ui.TrayHintShown = true;
        _ui.Save();
        return true;
    }
}

/// <summary>Настройки окна (не службы): в профиле пользователя.</summary>
public sealed class UiSettings
{
    private static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tropa", "ui.json");

    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public bool Notify { get; set; } = true;
    public bool CheckAppUpdates { get; set; } = true;
    /// <summary>Версия, которую человек отложил кнопкой «Позже»: про неё больше не напоминаем.</summary>
    public string? SkippedVersion { get; set; }

    public static UiSettings Load()
    {
        try { return File.Exists(Path_) ? JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(Path_)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
            File.WriteAllText(Path_, JsonSerializer.Serialize(this));
        }
        catch (IOException) { /* настройка окна не стоит падения */ }
    }
}

/// <summary>Запуск окна вместе с Windows: ключ Run текущего пользователя, окно стартует свёрнутым в трей.</summary>
public static class Autostart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue("Tropa") is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue("Tropa", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("Tropa", throwOnMissingValue: false);
    }
}
