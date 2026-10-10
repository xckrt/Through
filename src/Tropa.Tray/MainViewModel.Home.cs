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
    private string _zapretStatus = "Проверяю", _zapretDetail = "", _tgStatus = "Выключен", _tgDetail = "";
    private string _homeHeadline = "Проверяю, что открывается", _greeting = "";
    private bool _homeHooked;

    public OrbState ZapretOrb { get => _zapretOrb; private set => Set(ref _zapretOrb, value); }
    public string ZapretStatus { get => _zapretStatus; private set => Set(ref _zapretStatus, value); }
    public string ZapretDetail { get => _zapretDetail; private set => Set(ref _zapretDetail, value); }
    public OrbState TgOrb { get => _tgOrb; private set => Set(ref _tgOrb, value); }
    public string TgStatus { get => _tgStatus; private set => Set(ref _tgStatus, value); }
    public string TgDetail { get => _tgDetail; private set => Set(ref _tgDetail, value); }
    public string HomeHeadline { get => _homeHeadline; private set => Set(ref _homeHeadline, value); }
    public string Greeting { get => _greeting; private set => Set(ref _greeting, value); }

    /// <summary>Сервисы без контрольных — для «значков» под кнопкой обхода.</summary>
    public ObservableCollection<ServiceVm> HomeServices { get; } = [];

    /// <summary>«Подобрать заново» под кнопкой: когда обход уже настраивали.</summary>
    public bool CanRetune => _client is not null && _status is { Profile.Count: > 0 } && !IsRunning;
    public bool TgConnectVisible => TgOn;

    /// <summary>Голос Discord не проходит под защитой: кнопка «Подобрать голос».</summary>
    public bool VoiceBroken => _protecting && _client is not null && _last.Any(t => t.Voice && !t.IsOk) && !IsRunning;

    public ICommand TuneVoiceCommand => _tuneVoice ??= new RelayCommand(async () =>
    {
        IsRunning = true;
        try
        {
            Headline = "Подбираю обход";
            Subline = "Перебираю варианты голосового профиля: каждый проверяю как вход в голосовой канал. Около минуты.";
            ApplyStatus(await _client!.CallAsync<StatusReport>("tune.voice", ct: _cts.Token));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException)
        {
            Log($"голос: {ex.Message}", error: true);
        }
        finally
        {
            IsRunning = false;
        }
    }, () => !IsRunning && _client is not null);
    private ICommand? _tuneVoice;

    /// <summary>«Сеть «Дом» · своя стратегия»: у каждой сети свой подобранный обход.</summary>
    public string ZapretNetwork => _status?.Network is { } n
        ? $"Сеть «{n}»" + (_status.KnownNetworks > 1 ? $" · своя стратегия для каждой из {_status.KnownNetworks} сетей" : "")
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
            Log("прокси Telegram выключен; если Telegram был подключён к нему, отключите прокси и в самом Telegram");
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
        Greeting = hour switch { < 5 => "Доброй ночи", < 12 => "Доброе утро", < 18 => "Добрый день", _ => "Добрый вечер" };

        var services = Services.Where(s => !s.IsControl).ToList();
        var pending = services.Count == 0 || services.Any(s => s.Health == Health.Pending);
        var broken = services.Where(s => s.Health == Health.Blocked).Select(s => s.Title).ToList();
        var slow = services.Where(s => s.Health == Health.Slow).Select(s => s.Title).ToList();
        var tuning = _status?.State == ControllerState.Tuning || Headline == "Подбираю обход" || Headline.StartsWith("Устанавливаю") || Headline.StartsWith("Обновляю службу");
        var netDown = _status?.NetworkDown == true || Headline == "Нет подключения к интернету";

        // ---- обход
        if (IsRunning && tuning)
        {
            ZapretOrb = OrbState.Busy;
            ZapretStatus = Headline.StartsWith("Подбираю") ? "Подбираю" : "Готовлю службу";
            ZapretDetail = Subline;
        }
        else if (netDown)
        {
            ZapretOrb = _protecting ? OrbState.Problem : OrbState.Off;
            ZapretStatus = "Нет сети";
            ZapretDetail = "Контрольные сайты не открываются: дело не в блокировке. Как только сеть появится, Тропа проверит снова.";
        }
        else if (_protecting)
        {
            ZapretOrb = broken.Count > 0 ? OrbState.Problem : OrbState.On;
            ZapretStatus = broken.Count > 0 ? "Не всё работает" : "Включён";
            ZapretDetail = broken.Count > 0
                ? $"{Wording.JoinAnd(broken)} не {(broken.Count == 1 ? "открывается" : "открываются")} даже с обходом. Служба подберёт заново сама, или нажмите «Подобрать заново»."
                : pending ? "Проверяю, всё ли открывается…"
                : "Блокировки обходятся. Служба держит обход после перезагрузки и сама подбирает заново, если провайдер что-то поменял.";
        }
        else if (_status is { ProtectionWanted: true } && _client is not null)
        {
            ZapretOrb = OrbState.Problem;
            ZapretStatus = "Не запустился";
            ZapretDetail = _status.Message ?? "Служба не смогла запустить winws. Нажмите, чтобы попробовать снова.";
        }
        else
        {
            ZapretOrb = OrbState.Off;
            var ready = _client is not null && _status is { Profile.Count: > 0 };
            ZapretStatus = ready ? "Выключен" : "Не настроен";
            ZapretDetail = ready ? "Стратегии уже подобраны: включится за пару секунд."
                : pending ? "Проверяю, что режут…"
                : broken.Count > 0
                    ? $"{Wording.JoinAnd(broken)} {(broken.Count == 1 ? "не открывается" : "не открываются")}. Нажмите — Тропа сама подберёт обход за пару минут" + (_client is null ? " (Windows один раз спросит разрешение)." : ".")
                    : "Сейчас всё открывается и так. Если что-то начнёт тормозить, нажмите — Тропа подберёт обход.";
        }
        if (slow.Count > 0 && ZapretOrb != OrbState.Busy && broken.Count == 0)
            ZapretDetail += $" {Wording.JoinAnd(slow)} — медленно.";

        // ---- Telegram
        var t = _tg;
        if (_tgBusy && t is not { Running: true })
        {
            TgOrb = OrbState.Busy;
            TgStatus = "Включаю";
            TgDetail = TgSubline;
        }
        else if (t is not { Enabled: true })
        {
            TgOrb = OrbState.Off;
            TgStatus = "Выключен";
            TgDetail = "Нажмите — Тропа поднимет прокси и сама добавит его в Telegram. Telegram будет ходить через веб-версию, которую режут реже.";
        }
        else if (!t.Running)
        {
            TgOrb = OrbState.Problem;
            TgStatus = "Не запустился";
            TgDetail = t.Error ?? "Служба не смогла открыть порт.";
        }
        else
        {
            var failed = t.Checks.Count(c => !c.Ok);
            TgOrb = _tgBusy ? OrbState.Busy : failed == 0 ? OrbState.On : OrbState.Problem;
            TgStatus = _tgBusy ? "Проверяю" : failed == 0 ? "Работает" : failed == t.Checks.Length ? "Не отвечает" : "С перебоями";
            TgDetail = t.Stats is { Active: > 0 } st
                ? $"Telegram подключён: соединений сейчас {st.Active}, трафик {Bytes(st.BytesUp + st.BytesDown)}."
                : t.Stats is { Total: > 0 }
                    ? "Telegram подключён к прокси. Сейчас он молчит — откроет соединения, когда понадобится."
                    : "Прокси работает, осталось подключить Telegram: нажмите «Подключить в Telegram».";
            if (failed > 0) TgDetail = $"Не отвечают {string.Join(", ", t.Checks.Where(c => !c.Ok).Select(c => $"DC{c.Dc}"))}. " + TgDetail;
        }

        // ---- общий заголовок
        HomeHeadline = (ZapretOrb, TgOrb) switch
        {
            (OrbState.Busy, _) or (_, OrbState.Busy) => "Минутку, Тропа работает",
            _ when netDown => "Нет подключения к интернету",
            (OrbState.Problem, _) or (_, OrbState.Problem) => "Кое-что требует внимания",
            (OrbState.On, OrbState.On) => "Интернет без лишних ограничений",
            (OrbState.On, _) => "Обход работает",
            (_, OrbState.On) => "Telegram работает через Тропу",
            _ when pending => "Проверяю, что открывается",
            _ when broken.Count > 0 => "Часть сервисов режут",
            _ => "Всё открывается и так",
        };

        Raise(nameof(CanRetune));
        Raise(nameof(VoiceBroken));
        Raise(nameof(ZapretNetwork));
        Raise(nameof(HasZapretNetwork));
        Raise(nameof(TgConnectVisible));
    }
}
