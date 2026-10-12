using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using Through.Core.Control;
using Through.Core.Probing;

namespace Through.Tray;

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
    private string _pillText = Loc.T("служба не установлена");

    public Pill Pill { get => _pill; private set => Set(ref _pill, value); }
    public string PillText { get => _pillText; private set => Set(ref _pillText, value); }

    // ---------- журнал

    public ObservableCollection<LogLine> LogLines { get; } = [];

    private void Log(string text, bool error = false)
    {
        LogLines.Insert(0, new LogLine(DateTime.Now.ToString("HH:mm:ss"), Loc.Msg(text), error));
        while (LogLines.Count > 500) LogLines.RemoveAt(LogLines.Count - 1);
    }

    public ICommand OpenServiceLogCommand => _openLog ??= new RelayCommand(() =>
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Through", "service.log");
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        else Log(Loc.T("журнала службы ещё нет: служба не запускалась"));
        return Task.CompletedTask;
    });
    private ICommand? _openLog;

    private string TitleOf(string key) => Services.FirstOrDefault(s => s.Key == key)?.Title ?? Wording.ServiceTitle(key);

    // ---------- приложения

    public ObservableCollection<AppRowVm> AppRows { get; } = [];
    private IReadOnlyList<AppDefinition> _userApps = [];

    public IReadOnlyList<AppTemplate> Templates { get; } =
    [
        new("Dead by Daylight", Loc.T("Серверы игры на AWS и CloudFront, античит EasyAntiCheat на Cloudflare. Обход включается всегда: игра чаще тормозит, чем не открывается."),
            new AppDefinition
            {
                Key = "dbd", Title = "Dead by Daylight",
                Domains = ["bhvrdbd.com", "bhvronline.com", "easyanticheat.net", "eac-cdn.com", "epicgames.dev"],
                CheckHosts = ["steam.live.bhvrdbd.com", "cdn.live.dbd.bhvronline.com", "easyanticheat.net"],
                Always = true,
            }),
        new("Roblox", Loc.T("Сайт и лаунчер. Если тормозит сама игра, добавьте её UDP-порты."),
            new AppDefinition
            {
                Key = "roblox", Title = "Roblox",
                Domains = ["roblox.com", "rbxcdn.com", "robloxlabs.com", "rbx.com"],
                CheckHosts = ["www.roblox.com", "apis.roblox.com"],
            }),
        new("Twitch", Loc.T("Сайт, чат и видеопоток."),
            new AppDefinition
            {
                Key = "twitch", Title = "Twitch",
                Domains = ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net", "live-video.net"],
                CheckHosts = ["www.twitch.tv", "static-cdn.jtvnw.net"],
            }),
        new(Loc.T("Своё приложение"), Loc.T("Впишите домены, с которыми работает приложение. Поддомены обходятся сами."),
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
            Log(Loc.F("приложение «{0}» удалено", TitleOf(key)));
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
            Log(Loc.F("добавлено приложение «{0}»", app.Title));
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
            AppRows.Add(Row(key, Wording.ServiceTitle(key), domains > 0 ? Loc.F("встроенный, доменов: {0}", domains) : Loc.T("встроенный"), false));
        }
        foreach (var a in _userApps)
        {
            var parts = new List<string> { Loc.F("доменов: {0}", a.Domains.Length) };
            if (a.UdpPorts.Length > 0) parts.Add($"UDP {string.Join(",", a.UdpPorts)}");
            if (a.TcpPorts.Length > 0) parts.Add($"TCP {string.Join(",", a.TcpPorts)}");
            if (a.Always) parts.Add(Loc.T("обход всегда"));
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
            row.Summary = Loc.T("ещё не проверялось");
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
    private string _engineText = Loc.T("zapret ещё не скачан");
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

    private bool _gameFilter;
    private int _gameFilterVariant;
    private int _gameFilterVariants;

    /// <summary>Игровой фильтр: широкий диапазон UDP-портов, чтобы работали игры и голос без настройки каждой игры.</summary>
    public bool GameFilter
    {
        get => _gameFilter;
        set
        {
            if (!Set(ref _gameFilter, value)) return;
            Raise(nameof(HasGameVariants));
            if (_syncingSettings || _client is null) return;
            _ = CallQuietly("settings.gamefilter", new { on = value, variant = _gameFilterVariant });
        }
    }

    public int GameFilterVariant { get => _gameFilterVariant; private set => Set(ref _gameFilterVariant, value); }
    public int GameFilterVariants { get => _gameFilterVariants; private set { Set(ref _gameFilterVariants, value); Raise(nameof(HasGameVariants)); } }

    /// <summary>Есть из чего выбирать и фильтр включён: тогда показываем выбор варианта стратегии.</summary>
    public bool HasGameVariants => _gameFilter && _gameFilterVariants > 1;

    /// <summary>Выбрать вариант игровой стратегии: если игра не идёт, человек перебирает варианты.</summary>
    public ICommand SetGameVariantCommand => _setGameVariant ??= new ParamCommand(p =>
    {
        if (_syncingSettings || _client is null || p is not string s || !int.TryParse(s, out var v) || v == _gameFilterVariant) return Task.CompletedTask;
        GameFilterVariant = v;
        return CallQuietly("settings.gamefilter", new { on = true, variant = v });
    });
    private ICommand? _setGameVariant;

    // ---- авто-подбор игровой стратегии: Сквозь включает варианты по очереди, человек говорит, заработало ли
    private int _gameAutoStep; // 0 — не идёт, иначе номер проверяемого варианта (с 1)
    private string _gameAutoResult = "";

    public bool GameAutoActive => _gameAutoStep > 0;
    public string GameAutoText => _gameAutoStep > 0
        ? Loc.F("Включён вариант {0} из {1}. Зайдите в игру и попробуйте сыграть (зайти в матч). Работает?", _gameAutoStep, _gameFilterVariants)
        : "";
    public string GameAutoResult { get => _gameAutoResult; private set { Set(ref _gameAutoResult, value); Raise(nameof(HasGameAutoResult)); } }
    public bool HasGameAutoResult => _gameAutoResult.Length > 0;

    private void SetGameStep(int step)
    {
        _gameAutoStep = step;
        Raise(nameof(GameAutoActive));
        Raise(nameof(GameAutoText));
        Raise(nameof(HasGameVariants));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Начать авто-подбор: включаем первый вариант и просим человека проверить игру.</summary>
    public ICommand StartGameAutoCommand => _startGameAuto ??= new RelayCommand(async () =>
    {
        if (_client is null || _gameFilterVariants < 1) return;
        GameAutoResult = "";
        GameFilterVariant = 0;
        await CallQuietly("settings.gamefilter", new { on = true, variant = 0 });
        SetGameStep(1);
    }, () => !GameAutoActive && HasService && _gameFilterVariants > 1);
    private ICommand? _startGameAuto;

    /// <summary>Заработало: оставляем текущий вариант.</summary>
    public ICommand GameAutoWorksCommand => _gameAutoWorks ??= new RelayCommand(() =>
    {
        GameAutoResult = Loc.F("Готово: подошёл вариант {0}. Он и останется включённым.", _gameAutoStep);
        Raise(nameof(HasGameAutoResult));
        Log(Loc.F("игровой фильтр: подошёл вариант {0}", _gameAutoStep));
        SetGameStep(0);
        return Task.CompletedTask;
    }, () => GameAutoActive);
    private ICommand? _gameAutoWorks;

    /// <summary>Не заработало: следующий вариант, а если они кончились — подсказываем ручной путь.</summary>
    public ICommand GameAutoNextCommand => _gameAutoNext ??= new RelayCommand(async () =>
    {
        if (_client is null) return;
        if (_gameAutoStep < _gameFilterVariants)
        {
            var next = _gameAutoStep; // текущий шаг (с 1) = индекс следующего варианта (с 0)
            GameFilterVariant = next;
            await CallQuietly("settings.gamefilter", new { on = true, variant = next });
            SetGameStep(_gameAutoStep + 1);
        }
        else
        {
            GameAutoResult = Loc.T("Ни один вариант не подошёл. Попробуйте «Выбрать запущенную программу», выбрать игру и время 5 минут — Сквозь настроит обход прямо под неё.");
            Raise(nameof(HasGameAutoResult));
            SetGameStep(0);
        }
    }, () => GameAutoActive);
    private ICommand? _gameAutoNext;

    private bool _communityUse, _communityShare;

    /// <summary>Общая база настроена в этой сборке (адрес в community.json).</summary>
    public bool HasCommunity => _status?.Community is not null;

    public bool CommunityUse
    {
        get => _communityUse;
        set
        {
            if (!Set(ref _communityUse, value) || _syncingSettings || _client is null) return;
            _ = CallQuietly("settings.community", new { use = value, share = _communityShare });
        }
    }

    public bool CommunityShare
    {
        get => _communityShare;
        set
        {
            if (!Set(ref _communityShare, value) || _syncingSettings || _client is null) return;
            _ = CallQuietly("settings.community", new { use = _communityUse, share = value });
        }
    }

    public LanguageChoice Language => _ui.Language;

    /// <summary>Сменить язык: сохранить и перезапустить окно (строки XAML переводятся при загрузке).</summary>
    public ICommand SetLanguageCommand => _setLanguage ??= new ParamCommand(p =>
    {
        if (p is not string name || !Enum.TryParse<LanguageChoice>(name, out var choice) || choice == _ui.Language) return Task.CompletedTask;
        _ui.Language = choice;
        _ui.Save();
        Raise(nameof(Language));
        var exe = Environment.ProcessPath;
        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }
        return Task.CompletedTask;
    });
    private ICommand? _setLanguage;

    /// <summary>Сводки об ошибках: и окна (ui.json), и службы (её settings.json).</summary>
    public bool ShareCrashes
    {
        get => _ui.ShareCrashes;
        set
        {
            if (_ui.ShareCrashes == value) return;
            _ui.ShareCrashes = value;
            _ui.Save();
            Raise(nameof(ShareCrashes));
            if (_client is not null && !_syncingSettings) _ = CallQuietly("settings.crashes", new { on = value });
        }
    }

    public bool Autostart
    {
        get => _autostart;
        set
        {
            if (!Set(ref _autostart, value)) return;
            try { Tray.Autostart.Set(value); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Log(Loc.F("автозапуск: {0}", ex.Message), error: true); }
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
    public string BuildText => Loc.F("Сквозь, сборка {0}", Core.ThroughBuild.Id);
    public bool HasService => _client is not null;
    public bool CanUninstall => HasService && !IsPortable;

    public ICommand CheckUpdatesCommand => _checkUpdates ??= new RelayCommand(async () =>
    {
        if (_client is null) return;
        IsRunning = true;
        UpdateText = Loc.T("Проверяю…");
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
        var exe = Path.Combine(AppContext.BaseDirectory, "Through.Service.exe");
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "uninstall") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            if (p is not null) await p.WaitForExitAsync();
            Log(Loc.T("служба Сквозь удалена, winws остановлен"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Log(Loc.T("удаление службы отменено"));
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
        GameFilterVariant = s.GameFilterVariant;
        GameFilterVariants = s.GameFilterVariants;
        GameFilter = s.GameFilter;
        CommunityUse = s.Community?.Use ?? false;
        CommunityShare = s.Community?.Share ?? false;
        Raise(nameof(HasCommunity));
        _syncingSettings = false;
        EngineText = s.EngineVersion is { } v ? $"zapret {v}" : Loc.T("zapret скачается при первом подборе");
        if (s.LastUpdateCheck is { } at && (UpdateText == "" || UpdateText == Loc.T("Проверяю…")))
            UpdateText = Loc.F("Последняя проверка обновлений: {0:dd.MM HH:mm}", at.LocalDateTime);
        Raise(nameof(HasService));
        Raise(nameof(CanUninstall));

        (Pill, PillText) = s switch
        {
            { State: ControllerState.Tuning } => (Pill.Busy, Loc.T("подбираю обход")),
            { Protecting: true } when Services.Any(x => !x.IsControl && x.Health == Health.Blocked) => (Pill.Warn, Loc.T("защита включена, не всё работает")),
            { Protecting: true } => (Pill.On, Loc.T("защита включена")),
            { ProtectionWanted: true } => (Pill.Warn, Loc.T("защита не запустилась")),
            _ => (Pill.Off, Loc.T("защита выключена")),
        };
    }

    /// <summary>Служба сделала что-то сама или что-то сломалось: окно покажет уведомление у часов.</summary>
    public event Action<string, string>? Notice;

    /// <summary>Заголовок уведомления для сообщений службы, о которых стоит сказать, пока окно закрыто.</summary>
    private static string? Notable(string msg) =>
        msg.Contains("перестало работать") ? Loc.T("Сквозь подбирает обход заново")
        : msg.Contains("zapret обновлён") ? Loc.T("zapret обновлён")
        : msg.Contains("остановился сам") ? Loc.T("Сквозь перезапустила движок")
        : msg.Contains("не удалось включить защиту") || msg.Contains("не запустился") ? Loc.T("Сквозь: нужна помощь")
        : msg.StartsWith("подбор закончен") ? Loc.T("Подбор закончен")
        : msg.StartsWith("ранний сигнал") ? Loc.T("Сквозь: провайдер что-то поменял")
        : msg.StartsWith("Telegram: не отвечают") || msg.StartsWith("прокси Telegram не запустился") ? "Telegram"
        : msg.StartsWith("порт ") && msg.Contains("прокси Telegram") ? Loc.T("Telegram: подключите прокси заново")
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
    private static readonly string Path_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Through", "ui.json");

    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public LanguageChoice Language { get; set; } = LanguageChoice.System;
    public bool Notify { get; set; } = true;
    public bool CheckAppUpdates { get; set; } = true;
    /// <summary>Версия, которую человек отложил кнопкой «Позже»: про неё больше не напоминаем.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Отправлять обезличенные сводки об ошибках окна.</summary>
    public bool ShareCrashes { get; set; }

    /// <summary>Начало недели, сводку которой уже показали уведомлением.</summary>
    public string? LastWeekNotified { get; set; }

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
        return k?.GetValue("Through") is string;
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (on) k.SetValue("Through", $"\"{Environment.ProcessPath}\" --tray");
        else k.DeleteValue("Through", throwOnMissingValue: false);
    }
}
