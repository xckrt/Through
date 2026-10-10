using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using Tropa.Core.Control;

namespace Tropa.Tray;

/// <summary>
/// Обзор: две карточки — обход блокировок (zapret в службе) и прокси Telegram, у каждой одна большая кнопка.
/// Подробная проверка по этапам ушла на страницу «Диагностика»: человеку важно «работает или нет», а не стадии TLS.
/// </summary>
public sealed partial class MainViewModel
{
    private OrbState _zapretOrb, _tgOrb;
    private string _zapretStatus = Loc.T("Проверяю"), _zapretDetail = "", _tgStatus = Loc.T("Выключен"), _tgDetail = "";
    private string _homeHeadline = Loc.T("Проверяю, что открывается"), _greeting = "";
    private bool _homeHooked;

    public OrbState ZapretOrb { get => _zapretOrb; private set => Set(ref _zapretOrb, value); }
    public string ZapretStatus { get => _zapretStatus; private set => Set(ref _zapretStatus, value); }
    public string ZapretDetail { get => _zapretDetail; private set => Set(ref _zapretDetail, Loc.Msg(value)); }
    public OrbState TgOrb { get => _tgOrb; private set => Set(ref _tgOrb, value); }
    public string TgStatus { get => _tgStatus; private set => Set(ref _tgStatus, value); }
    public string TgDetail { get => _tgDetail; private set => Set(ref _tgDetail, Loc.Msg(value)); }
    public string HomeHeadline { get => _homeHeadline; private set => Set(ref _homeHeadline, value); }
    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }

    /// <summary>Сервисы без контрольных — для «значков» под кнопкой обхода.</summary>
    public ObservableCollection<ServiceVm> HomeServices { get; } = [];

    /// <summary>«Подобрать заново» под кнопкой: когда обход уже настраивали.</summary>
    public bool CanRetune => _client is not null && _status is { Profile.Count: > 0 } && !IsRunning;
    public bool TgConnectVisible => TgOn;

    /// <summary>«YouTube: 0,8 → 92 Мбит/с» — до обхода и с ним.</summary>
    public string SpeedText
    {
        get
        {
            static string F(Tropa.Core.Probing.SpeedResult? r) => r is null ? "?" : r.Error is not null ? Loc.T("не открывается") : $"{r.Mbps:0.#}";
            var s = _status?.Speed;
            if (s?.With is null && s?.Without is null) return "";
            if (s.Without is not null && s.With is not null) return Loc.F("YouTube: {0} → {1} Мбит/с", F(s.Without), F(s.With));
            return s.With is not null ? Loc.F("YouTube с обходом: {0} Мбит/с", F(s.With)) : Loc.F("YouTube без обхода: {0} Мбит/с", F(s.Without));
        }
    }
    public bool HasSpeed => SpeedText.Length > 0;
    private bool _speedBusy;

    public ICommand MeasureSpeedCommand => _measureSpeed ??= new RelayCommand(async () =>
    {
        _speedBusy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            Log(Loc.T("замеряю скорость YouTube…"));
            ApplyStatus(await _client!.CallAsync<StatusReport>("speed", ct: _cts.Token));
            Raise(nameof(SpeedText));
            Raise(nameof(HasSpeed));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException) { Log(Loc.F("скорость: {0}", ex.Message), error: true); }
        finally
        {
            _speedBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }, () => !_speedBusy && !IsRunning && _client is not null);
    private ICommand? _measureSpeed;

    /// <summary>Голос Discord не проходит под защитой: кнопка «Подобрать голос».</summary>
    public bool VoiceBroken => _protecting && _client is not null && _last.Any(t => t.Voice && !t.IsOk) && !IsRunning;

    public ICommand TuneVoiceCommand => _tuneVoice ??= new RelayCommand(async () =>
    {
        IsRunning = true;
        try
        {
            Headline = Loc.T("Подбираю обход");
            Subline = Loc.T("Перебираю варианты голосового профиля: каждый проверяю как вход в голосовой канал. Около минуты.");
            ApplyStatus(await _client!.CallAsync<StatusReport>("tune.voice", ct: _cts.Token));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            Log(Loc.F("голос: {0}", ex.Message), error: true);
        }
        finally
        {
            IsRunning = false;
        }
    }, () => !IsRunning && _client is not null);
    private ICommand? _tuneVoice;

    /// <summary>«Сеть «Дом» · своя стратегия»: у каждой сети свой подобранный обход.</summary>
    public string ZapretNetwork => _status?.Network is { } n
        ? Loc.F("Сеть «{0}»", n) + (_status.KnownNetworks > 1 ? Loc.F(" · своя стратегия для каждой из {0} сетей", _status.KnownNetworks) : "")
        : "";
    public bool HasZapretNetwork => ZapretNetwork.Length > 0;

    public ICommand ZapretToggleCommand => _zapretToggle ??= new RelayCommand(async () =>
    {
        if (IsRunning) return;
        if (_client is not null && (_protecting || _status is { Profile.Count: > 0 })) await ProtectAsync();
        else await TuneAsync(null);
    }, () => !IsRunning);
    private ICommand? _zapretToggle;

    public ICommand TgToggleCommand => _tgToggle ??= new RelayCommand(async () =>
    {
        if (_tg is { Enabled: true } && _client is not null)
        {
            await TelegramCallAsync("telegram", new { on = false });
            Log(Loc.T("прокси Telegram выключен; если Telegram был подключён к нему, отключите прокси и в самом Telegram"));
        }
        else await EnableTelegramAsync();
    }, () => !_tgBusy && !IsRunning);
    private ICommand? _tgToggle;

    public ICommand OpenDiagnosticsCommand => _openDiag ??= new RelayCommand(() =>
    {
        Page = Pages.Diagnostics;
        return Task.CompletedTask;
    });
    private ICommand? _openDiag;

    private void HookHome()
    {
        if (_homeHooked) return;
        _homeHooked = true;
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Headline) or nameof(Subline) or nameof(IsRunning) or nameof(Pill)
                or nameof(TgHeadline) or nameof(TgSubline) or nameof(TgHealth) or nameof(TgOn) or nameof(TgStats) or nameof(ProtectLabel))
                RefreshHome();
        };
        Services.CollectionChanged += OnServicesChanged;
        RefreshHome();
    }

    private void OnServicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HomeServices.Clear();
        foreach (var s in Services.Where(s => !s.IsControl))
        {
            HomeServices.Add(s);
            s.PropertyChanged -= OnServiceHealth;
            s.PropertyChanged += OnServiceHealth;
        }
        RefreshHome();
    }

    private void OnServiceHealth(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServiceVm.Health)) RefreshHome();
    }

    private void RefreshHome()
    {
        var hour = DateTime.Now.Hour;
        Greeting = hour switch { < 5 => Loc.T("Доброй ночи"), < 12 => Loc.T("Доброе утро"), < 18 => Loc.T("Добрый день"), _ => Loc.T("Добрый вечер") };

        var services = Services.Where(s => !s.IsControl).ToList();
        var pending = services.Count == 0 || services.Any(s => s.Health == Health.Pending);
        var broken = services.Where(s => s.Health == Health.Blocked).Select(s => s.Title).ToList();
        var slow = services.Where(s => s.Health == Health.Slow).Select(s => s.Title).ToList();
        var tuning = _status?.State == ControllerState.Tuning || Headline == Loc.T("Подбираю обход") || Headline == Loc.T("Устанавливаю службу Тропы") || Headline == Loc.T("Обновляю службу Тропы") || Headline == Loc.T("Запускаю Тропу");
        var netDown = _status?.NetworkDown == true || Headline == Loc.T("Нет подключения к интернету");

        // ---- обход
        if (IsRunning && tuning)
        {
            ZapretOrb = OrbState.Busy;
            ZapretStatus = Headline == Loc.T("Подбираю обход") ? Loc.T("Подбираю") : Loc.T("Готовлю службу");
            ZapretDetail = Subline;
        }
        else if (netDown)
        {
            ZapretOrb = _protecting ? OrbState.Problem : OrbState.Off;
            ZapretStatus = Loc.T("Нет сети");
            ZapretDetail = Loc.T("Контрольные сайты не открываются: дело не в блокировке. Как только сеть появится, Тропа проверит снова.");
        }
        else if (_protecting)
        {
            ZapretOrb = broken.Count > 0 ? OrbState.Problem : OrbState.On;
            ZapretStatus = broken.Count > 0 ? Loc.T("Не всё работает") : Loc.T("Включён");
            ZapretDetail = broken.Count > 0
                ? Loc.F("{0} не {1} даже с обходом. Служба подберёт заново сама, или нажмите «Подобрать заново».", Wording.JoinAnd(broken), (broken.Count == 1 ? Loc.T("открывается") : Loc.T("открываются")))
                : pending ? Loc.T("Проверяю, всё ли открывается…")
                : Loc.T("Блокировки обходятся. Служба держит обход после перезагрузки и сама подбирает заново, если провайдер что-то поменял.");
        }
        else if (_status is { ProtectionWanted: true } && _client is not null)
        {
            ZapretOrb = OrbState.Problem;
            ZapretStatus = Loc.T("Не запустился");
            ZapretDetail = _status.Message ?? Loc.T("Служба не смогла запустить winws. Нажмите, чтобы попробовать снова.");
        }
        else
        {
            ZapretOrb = OrbState.Off;
            var ready = _client is not null && _status is { Profile.Count: > 0 };
            ZapretStatus = ready ? Loc.T("Выключен") : Loc.T("Не настроен");
            ZapretDetail = ready ? Loc.T("Стратегии уже подобраны: включится за пару секунд.")
                : pending ? Loc.T("Проверяю, что режут…")
                : broken.Count > 0
                    ? Loc.F("{0} {1}. Нажмите — Тропа сама подберёт обход за пару минут", Wording.JoinAnd(broken), (broken.Count == 1 ? Loc.T("не открывается") : Loc.T("не открываются"))) + (_client is null ? Loc.T(" (Windows один раз спросит разрешение).") : ".")
                    : Loc.T("Сейчас всё открывается и так. Если что-то начнёт тормозить, нажмите — Тропа подберёт обход.");
        }
        if (slow.Count > 0 && ZapretOrb != OrbState.Busy && broken.Count == 0)
            ZapretDetail += Loc.F(" {0} — медленно.", Wording.JoinAnd(slow));

        // ---- Telegram
        var t = _tg;
        if (_tgBusy && t is not { Running: true })
        {
            TgOrb = OrbState.Busy;
            TgStatus = Loc.T("Включаю");
            TgDetail = TgSubline;
        }
        else if (t is not { Enabled: true })
        {
            TgOrb = OrbState.Off;
            TgStatus = Loc.T("Выключен");
            TgDetail = Loc.T("Нажмите — Тропа поднимет прокси и сама добавит его в Telegram. Telegram будет ходить через веб-версию, которую режут реже.");
        }
        else if (!t.Running)
        {
            TgOrb = OrbState.Problem;
            TgStatus = Loc.T("Не запустился");
            TgDetail = t.Error ?? Loc.T("Служба не смогла открыть порт.");
        }
        else
        {
            var failed = t.Checks.Count(c => !c.Ok);
            TgOrb = _tgBusy ? OrbState.Busy : failed == 0 ? OrbState.On : OrbState.Problem;
            TgStatus = _tgBusy ? Loc.T("Проверяю") : failed == 0 ? Loc.T("Работает") : failed == t.Checks.Length ? Loc.T("Не отвечает") : Loc.T("С перебоями");
            TgDetail = t.Stats is { Active: > 0 } st
                ? Loc.F("Telegram подключён: соединений сейчас {0}, трафик {1}.", st.Active, Bytes(st.BytesUp + st.BytesDown))
                : t.Stats is { Total: > 0 }
                    ? Loc.T("Telegram подключён к прокси. Сейчас он молчит — откроет соединения, когда понадобится.")
                    : Loc.T("Прокси работает, осталось подключить Telegram: нажмите «Подключить в Telegram».");
            if (failed > 0) TgDetail = Loc.F("Не отвечают {0}. ", string.Join(", ", t.Checks.Where(c => !c.Ok).Select(c => $"DC{c.Dc}"))) + TgDetail;
        }

        // ---- общий заголовок
        HomeHeadline = (ZapretOrb, TgOrb) switch
        {
            (OrbState.Busy, _) or (_, OrbState.Busy) => Loc.T("Минутку, Тропа работает"),
            _ when netDown => Loc.T("Нет подключения к интернету"),
            (OrbState.Problem, _) or (_, OrbState.Problem) => Loc.T("Кое-что требует внимания"),
            (OrbState.On, OrbState.On) => Loc.T("Интернет без лишних ограничений"),
            (OrbState.On, _) => Loc.T("Обход работает"),
            (_, OrbState.On) => Loc.T("Telegram работает через Тропу"),
            _ when pending => Loc.T("Проверяю, что открывается"),
            _ when broken.Count > 0 => Loc.T("Часть сервисов режут"),
            _ => Loc.T("Всё открывается и так"),
        };

        Raise(nameof(CanRetune));
        Raise(nameof(VoiceBroken));
        Raise(nameof(SpeedText));
        Raise(nameof(HasSpeed));
        Raise(nameof(ZapretNetwork));
        Raise(nameof(HasZapretNetwork));
        Raise(nameof(TgConnectVisible));
    }
}
