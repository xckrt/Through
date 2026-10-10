using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Tropa.Core.Control;
using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.Tray;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Func<Task> run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public async void Execute(object? parameter) => await run();
}

/// <summary>Partial — важное работает, не работает второстепенное (например, обновления клиента).</summary>
public enum Health { Pending, Ok, Slow, Blocked, Unknown, Partial }

public sealed class TargetVm(string service, string name, string host) : Observable
{
    private TrailState _state;
    private int _stopAt;
    private string _verdict = "";
    private string _metric = "";

    public string Service => service;
    public string Name => name;
    public string Host => host;
    public TargetReport? Result { get; private set; }

    public TrailState State { get => _state; private set => Set(ref _state, value); }
    public int StopAt { get => _stopAt; private set => Set(ref _stopAt, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string Metric { get => _metric; private set => Set(ref _metric, value); }

    public bool Matches(TargetReport r) => r.Service == service && r.Name == name && r.Host == host;

    public void Reset()
    {
        Result = null;
        State = TrailState.Pending;
        Verdict = "";
        Metric = "";
    }

    public void Apply(TargetReport r)
    {
        Result = r;
        StopAt = (int)(r.FailedAt ?? ProbeStage.Dns);
        State = r.Verdict switch
        {
            Core.Probing.Verdict.Ok => TrailState.Ok,
            Core.Probing.Verdict.Throttled => TrailState.Throttled,
            Core.Probing.Verdict.Blocked when r.Optional => TrailState.Minor,
            Core.Probing.Verdict.Blocked => TrailState.Broken,
            _ => TrailState.Unknown,
        };
        Verdict = r.Optional && r.Verdict == Core.Probing.Verdict.Blocked
            ? Loc.F("недоступно: {0}", r.Role is { } role ? Loc.T(role) : Loc.T("второстепенное"))
            : Wording.Explain(r);
        Metric = Wording.Metric(r);
    }
}

public sealed class ServiceVm : Observable
{
    private bool _expanded;
    private Health _health;
    private string _summary = "";
    private string? _planHeading;

    public ServiceVm(string key, IEnumerable<TargetVm> targets)
    {
        Key = key;
        _title = Wording.ServiceTitle(key);
        Targets = new(targets);
        IsControl = key == "control";
    }

    private string _title;

    public string Key { get; }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public bool IsControl { get; }
    public ObservableCollection<TargetVm> Targets { get; }
    public ObservableCollection<string> PlanItems { get; } = [];
    public ObservableCollection<string> Technical { get; } = [];
    public ServiceReport? Report { get; private set; }

    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public Health Health { get => _health; private set => Set(ref _health, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>Что второстепенное не работает (через запятую) — для общей сводки.</summary>
    public string MinorText { get; private set; } = "";

    public string? PlanHeading
    {
        get => _planHeading;
        private set { if (Set(ref _planHeading, value)) Raise(nameof(HasPlanHeading)); }
    }

    public bool HasPlanHeading => PlanHeading is not null;

    /// <summary>Все упавшие цели режут по имени сайта, а сам сервер отвечает.</summary>
    public bool SniFiltered => Targets.Where(t => t.Result is { Verdict: Verdict.Blocked }).All(t => t.Result!.SniFiltered == true);

    public void Reset()
    {
        foreach (var t in Targets) t.Reset();
        Report = null;
        PlanHeading = null;
        PlanItems.Clear();
        Technical.Clear();
        Refresh();
    }

    /// <summary>Сводка по целям; report (план или активная стратегия) приходит вместе с итогом проверки.</summary>
    public void Refresh(ServiceReport? report = null, Func<string, string>? presetName = null)
    {
        // здоровье сервиса — по важным проверкам; второстепенные дают только «работает, кроме…»
        var all = Targets.Select(t => t.Result).ToList();
        var results = all.Where(r => r is null || !r.Optional).ToList();
        var minor = all.Where(r => r is { Optional: true, Verdict: Verdict.Blocked }).Select(r => r!.Role is { } role ? Loc.T(role) : r.Host).ToList();
        Health = all.Any(r => r is null) ? Health.Pending
            : results.Any(r => r!.Verdict == Verdict.Blocked) ? Health.Blocked
            : results.Any(r => r!.Verdict == Verdict.Inconclusive) ? Health.Unknown
            : results.Any(r => r!.Verdict == Verdict.Throttled) ? Health.Slow
            : minor.Count > 0 ? Health.Partial
            : Health.Ok;

        var active = (report ?? Report)?.ActivePreset;
        Summary = Health switch
        {
            Health.Pending => Loc.T("проверяю"),
            Health.Ok when IsControl => Loc.T("сеть в порядке"),
            Health.Ok => active is null ? Loc.T("работает") : Loc.T("работает через обход"),
            Health.Slow => Loc.T("работает медленно"),
            Health.Partial => Loc.F("работает, кроме: {0}", MinorText = string.Join(", ", minor)),
            Health.Unknown => Loc.T("нет сети"),
            _ when active is not null => Loc.T("не работает даже с обходом"),
            _ when SniFiltered => Loc.T("режут по имени сайта"),
            _ when (report ?? Report)?.Plan == PlanStatus.NotTreatable => Loc.T("заблокирован по адресу"),
            _ => Loc.T("не работает"),
        };

        if (report is null) return;
        Report = report;
        if (report.Title is { Length: > 0 } title) Title = title;
        PlanItems.Clear();
        var name = presetName ?? (id => id);
        PlanHeading = active is { Length: > 0 }
            ? Loc.F("Работает через стратегию «{0}» ({1}).", name(active), active) + (report.Overrides.Count > 0 ? Loc.T(" Упрямым хостам Тропа подобрала свои:") : "")
            : active is not null
            ? Loc.T("Ни одна стратегия не взяла сервис целиком, поэтому у хостов свои:")
            : report.Plan switch
            {
                PlanStatus.NeedsTuning =>
                    Loc.F("Подборщик попробует {0} {1}, от простых к сложным:", report.Candidates.Length, Wording.Plural(report.Candidates.Length, "стратегию", "стратегии", "стратегий")),
                PlanStatus.NotTreatable =>
                    Loc.T("Соединение блокируют по адресу сервера. Настройка DPI-обработки тут не поможет, нужен VPN или прокси."),
                PlanStatus.NoCandidates => Loc.T("Для такой блокировки пока нет готовых стратегий."),
                _ => null,
            };
        if (active is null)
            foreach (var c in report.Candidates)
                PlanItems.Add(c.Name is null ? c.Id : $"{c.Name} ({c.Id})");
        else
            foreach (var (host, preset) in report.Overrides)
                PlanItems.Add(Loc.F("для {0} отдельно: «{1}» ({2})", host, name(preset), preset));

        Technical.Clear();
        foreach (var t in Targets)
            if (t.Result is { } r)
                Technical.Add($"{r.Host}{(r.Address is null ? "" : $" ({r.Address})")}: {(Loc.Msg(r.Detail) is { Length: > 0 } d ? d : Loc.T("без замечаний"))}");
    }
}

/// <summary>
/// Окно в двух режимах. Служба запущена: всё делает она (проверка, подбор, защита), окно только
/// показывает и просит по каналу. Службы нет: окно само гоняет пробник, подбор недоступен.
/// </summary>
public sealed partial class MainViewModel : Observable
{
    private readonly string _targetsPath;
    private readonly string _presetsPath;
    private readonly ProbeRunner _runner;
    private readonly string? _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private ControlClient? _client;
    private TargetCatalog? _catalog;
    private PresetCatalog? _presets;
    private StatusReport? _status;
    private IReadOnlyList<TargetReport> _last = [];
    private int _attempts;
    private bool _serviceOutdated;

    private string _headline = Loc.T("Проверяю сервисы");
    private string _subline = Loc.T("Обычно это занимает до 15 секунд.");
    private bool _running;
    private string _checkLabel = Loc.T("Проверить");
    private string _copyLabel = Loc.T("Копировать отчёт");
    private string _checkedAt = "";
    private string _modeNote = "";
    private bool _tuneVisible;
    private bool _protectVisible;
    private bool _protecting;

    /// <param name="options">Настройки пробника для режима без службы.</param>
    /// <param name="dataDir">Где лежат targets.json и presets.json; по умолчанию рядом с программой.</param>
    /// <param name="pipeName">Канал службы; null — не искать службу.</param>
    public MainViewModel(ProbeOptions? options = null, string? dataDir = null, string? pipeName = ControlProtocol.PipeName)
    {
        dataDir ??= AppContext.BaseDirectory;
        _targetsPath = Path.Combine(dataDir, "targets.json");
        _presetsPath = Path.Combine(dataDir, "presets.json");
        _runner = new ProbeRunner(new ServiceProber(options));
        _pipeName = pipeName;
        CheckCommand = new RelayCommand(CheckAsync, () => !IsRunning);
        CopyCommand = new RelayCommand(CopyAsync, () => !IsRunning && _last.Count > 0);
        TuneCommand = new RelayCommand(TuneAsync, () => !IsRunning);
        ProtectCommand = new RelayCommand(ProtectAsync, () => !IsRunning && _client is not null);
    }

    public ObservableCollection<ServiceVm> Services { get; } = [];
    public RelayCommand CheckCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand TuneCommand { get; }
    public RelayCommand ProtectCommand { get; }

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, Loc.Msg(value)); }
    public bool IsRunning { get => _running; private set { Set(ref _running, value); CommandManager.InvalidateRequerySuggested(); } }
    public string CheckLabel { get => _checkLabel; private set => Set(ref _checkLabel, value); }
    public string CopyLabel { get => _copyLabel; private set => Set(ref _copyLabel, value); }
    public string CheckedAt { get => _checkedAt; private set => Set(ref _checkedAt, value); }
    public string ModeNote { get => _modeNote; private set { if (Set(ref _modeNote, value)) Raise(nameof(HasModeNote)); } }
    public bool HasModeNote => ModeNote.Length > 0;
    public bool TuneVisible
    {
        get => _tuneVisible;
        private set { if (Set(ref _tuneVisible, value)) RaiseButtons(); }
    }

    public bool CheckIsPrimary => !TuneVisible;
    public bool TuneIsPrimary => TuneVisible && Services.Any(s => !s.IsControl && s.Health == Health.Blocked);
    public bool TuneIsSecondary => TuneVisible && !TuneIsPrimary;
    public bool ProtectVisible { get => _protectVisible; private set => Set(ref _protectVisible, value); }
    public string ProtectLabel => _protecting ? Loc.T("Выключить защиту") : Loc.T("Включить защиту");

    public void Cancel() => _cts.Cancel();

    private void RaiseButtons()
    {
        Raise(nameof(CheckIsPrimary));
        Raise(nameof(TuneIsPrimary));
        Raise(nameof(TuneIsSecondary));
    }

    /// <summary>Открытие окна: найти службу и сразу показать картину.</summary>
    public async Task StartAsync()
    {
        HookHome();
        _ = StartBannersAsync();
        if (_pipeName is not null && await ConnectAsync(TimeSpan.FromMilliseconds(400)) is { } status)
        {
            if (status.State == ControllerState.Idle) await CheckAsync();
            return;
        }

        TuneVisible = true;
        ModeNote = IsPortable
            ? Loc.T("Переносная версия: обход работает, пока открыта Тропа. При включении Windows спросит разрешение администратора.")
            : Loc.T("Обход настраивает служба Тропы. При первом подборе Windows один раз спросит разрешение администратора.");
        await CheckAsync();
    }

    /// <summary>Подключиться к службе и подписаться на её события. null, если служба не отвечает.</summary>
    private async Task<StatusReport?> ConnectAsync(TimeSpan timeout)
    {
        var client = await ControlClient.TryConnectAsync(_pipeName!, timeout, _cts.Token);
        if (client is null) return null;
        client.Event += (evt, data) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_client == client) OnServiceEvent(evt, data);
        });
        // старый клиент при обновлении службы отключается позже, чем подключится новый: его потерю не считаем
        client.Disconnected += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_client == client) OnServiceLost();
        });
        _client = client;
        try
        {
            var status = await client.CallAsync<StatusReport>("status", ct: _cts.Token);
            // служба из прошлой версии Тропы: обновим её при следующем подборе
            _serviceOutdated = status.Build != Core.TropaBuild.Id;
            ApplyStatus(status);
            CommandManager.InvalidateRequerySuggested();
            return status;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            OnServiceLost();
            return null;
        }
    }

    /// <summary>
    /// Ставит службу: Tropa.Service.exe install с запросом UAC (единственный раз, когда нужны права администратора),
    /// затем ждёт, пока она поднимет канал.
    /// </summary>
    private async Task InstallServiceAsync()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "Tropa.Service.exe");
        if (!File.Exists(exe))
            throw new InvalidOperationException(Loc.T("Рядом с программой нет Tropa.Service.exe. Переустановите Тропу."));

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(exe, "install")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // человек нажал «Нет» в окне UAC
        {
            throw new InvalidOperationException(
                Loc.T("Без разрешения администратора Тропа не может настроить обход: winws работает с сетевым драйвером."));
        }

        using (process)
        {
            await process.WaitForExitAsync(_cts.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(Loc.F("Служба не установилась: {0}", LastInstallError() ?? Loc.F("код {0}", process.ExitCode)));
        }

        for (var i = 0; i < 20; i++)
        {
            if (await ConnectAsync(TimeSpan.FromMilliseconds(500)) is not null) return;
            await Task.Delay(500, _cts.Token);
        }
        throw new InvalidOperationException(@"Служба установлена, но не отвечает. Подробности в %ProgramData%\Tropa\service.log.");
    }

    private static string? LastInstallError()
    {
        var log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tropa", "install.log");
        try
        {
            var last = File.Exists(log) ? File.ReadLines(log).LastOrDefault(l => l.Contains("ошибка:")) : null;
            return last?[(last.IndexOf("ошибка:", StringComparison.Ordinal) + Loc.T("ошибка:").Length)..].Trim();
        }
        catch (IOException) { return null; }
    }

    private async Task CheckAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        var label = CheckLabel;
        CheckLabel = Loc.T("Проверяю…");
        Headline = Loc.T("Проверяю сервисы");
        Subline = Loc.T("Обычно это занимает до 15 секунд.");
        try
        {
            if (_client is not null)
            {
                foreach (var s in Services) s.Reset();
                ApplyStatus(await _client.CallAsync<StatusReport>("check", ct: _cts.Token));
            }
            else
            {
                await CheckLocallyAsync();
            }
            label = Loc.T("Проверить снова");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Headline = Loc.T("Проверка не запустилась");
            Subline = ex.Message;
        }
        finally
        {
            CheckLabel = label;
            IsRunning = false;
        }
    }

    private async Task CheckLocallyAsync()
    {
        _catalog ??= TargetCatalog.Load(_targetsPath);
        var serviceOf = new Dictionary<ProbeTarget, string>();
        foreach (var (key, targets) in _catalog.Services)
            foreach (var t in targets) serviceOf.TryAdd(t, key);

        if (Services.Count == 0)
            Build(_catalog.Services.SelectMany(kv => kv.Value.Select(t => (kv.Key, t.Name, t.Host))));
        foreach (var s in Services) s.Reset();

        var progress = new Progress<ProbeResult>(r => ApplyTarget(TargetReport.From(serviceOf[r.Target], r)));
        var results = await _runner.RunAsync(_catalog.All, _cts.Token, progress);
        _last = results.Select(r => TargetReport.From(serviceOf[r.Target], r)).ToList();
        foreach (var r in _last) ApplyTarget(r);

        var networkDown = ProbeRunner.IsNetworkDown(results);
        var plans = networkDown ? [] : Tuner.Plan(_catalog, null, results, Presets());
        foreach (var s in Services)
        {
            var plan = plans.FirstOrDefault(p => p.Service == s.Key);
            s.Refresh(new ServiceReport
            {
                Key = s.Key,
                Plan = plan?.Status ?? PlanStatus.Ok,
                Candidates = plan?.Candidates.Select(c => new CandidateReport(c.Id, c.Name)).ToArray() ?? [],
            }, PresetName);
        }
        Summarize(networkDown, protecting: false);
        CheckedAt = Loc.F("Проверено в {0:HH:mm}", DateTime.Now);
    }

    private Task TuneAsync() => TuneAsync(null);

    /// <summary>Подобрать обход всем сервисам или только перечисленным (например, только что добавленному приложению).</summary>
    private async Task TuneAsync(string[]? services)
    {
        if (IsRunning) return;
        IsRunning = true;
        _attempts = 0;
        Page = Pages.Overview;
        try
        {
            await EnsureServiceAsync();
            Headline = Loc.T("Подбираю обход");
            Subline = _status?.EngineVersion is null
                ? Loc.T("Сначала Тропа скачает zapret с GitHub (около 8 МБ) и сверит хеши, затем переберёт стратегии. Это займёт несколько минут.")
                : Loc.T("Это займёт несколько минут. Пока идёт подбор, сервисы могут открываться с перебоями.");
            Log(Loc.T("подбор начат") + (services is null ? "" : $": {string.Join(", ", services.Select(Wording.ServiceTitle))}"));
            var summary = await _client!.CallAsync<TuneSummary>("tune", services is null ? null : new { services }, _cts.Token);
            ApplyStatus(await _client.CallAsync<StatusReport>("status", ct: _cts.Token));
            await RefreshAppsAsync();
            if (summary.NetworkDown)
            {
                Headline = Loc.T("Подбор отменён");
                Subline = Loc.T("Контрольные сайты недоступны: похоже, нет сети.");
            }
            Log(Loc.T("подбор закончен: ") + string.Join(", ", summary.Services.Select(x => $"{TitleOf(x.Service)} — {OutcomeText(x.Outcome)}")));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Headline = Loc.T("Подбор не удался");
            Subline = ex.Message;
            Log(Loc.T("подбор не удался: ") + ex.Message, error: true);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>Служба нужна для всего, что меняет сеть: поставить или обновить её (один запрос UAC).</summary>
    private async Task EnsureServiceAsync()
    {
        if (_client is not null && !_serviceOutdated) return;
        if (_pipeName is null) throw new InvalidOperationException(Loc.T("Служба Тропы недоступна."));

        // Служба могла быть жива, а трей просто не успел подключиться на старте (быстрый таймаут 400 мс)
        // или потерял связь на миг. Прежде чем переустанавливать — а это остановит и перезапустит службу —
        // несколько секунд пробуем просто подключиться, иначе один неудачный коннект гонит в цикл переустановок.
        if (!_serviceOutdated)
        {
            for (var i = 0; i < 8; i++)
            {
                if (await ConnectAsync(TimeSpan.FromMilliseconds(500)) is not null)
                {
                    if (!_serviceOutdated) return;
                    break; // подключились, но служба из прошлой версии — ниже обновим
                }
                await Task.Delay(300, _cts.Token);
            }
        }

        Headline = _serviceOutdated ? Loc.T("Обновляю службу Тропы") : Loc.T("Устанавливаю службу Тропы");
        Subline = _serviceOutdated
            ? Loc.T("Служба осталась от прошлой версии. Windows спросит разрешение администратора, чтобы её заменить.")
            : Loc.T("Windows спросит разрешение администратора. Оно нужно один раз: служба запускает winws, а тот работает с сетевым драйвером.");
        if (_client is { } old)
        {
            _client = null;
            await old.DisposeAsync();
        }
        if (IsPortable)
        {
            Headline = Loc.T("Запускаю Тропу");
            Subline = Loc.T("Windows спросит разрешение администратора: обход работает с сетевым драйвером. В переносной версии он держится, пока открыта Тропа.");
            await StartPortableHostAsync();
        }
        else await InstallServiceAsync();
        _serviceOutdated = false;
        Log(Headline == Loc.T("Обновляю службу Тропы") ? Loc.T("служба обновлена") : Loc.T("служба установлена"));
    }

    private static string OutcomeText(TuneOutcome o) => o switch
    {
        TuneOutcome.AlreadyOk => Loc.T("работает без обхода"),
        TuneOutcome.Fixed => Loc.T("починен"),
        TuneOutcome.Improved => Loc.T("стало лучше"),
        TuneOutcome.NotTreatable => Loc.T("блок по адресу"),
        TuneOutcome.NoCandidates => Loc.T("нет стратегий"),
        _ => Loc.T("не помогло"),
    };

    private async Task ProtectAsync()
    {
        if (_client is null || IsRunning) return;
        IsRunning = true;
        var switched = false;
        try
        {
            ApplyStatus(await _client.CallAsync<StatusReport>("protect", new { on = !_protecting }, _cts.Token));
            switched = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Subline = ex.Message;
        }
        finally
        {
            IsRunning = false;
        }
        // проверка после переключения: человек сразу видит, что изменилось
        if (switched) await CheckAsync();
    }

    // ---------- события службы и применение отчётов

    private void OnServiceEvent(string evt, JsonElement data)
    {
        switch (evt)
        {
            case "probe":
                ApplyTarget(ControlProtocol.FromData<TargetReport>(data));
                break;
            case "attempt":
                var a = ControlProtocol.FromData<AttemptReport>(data);
                _attempts++;
                var who = a.Host is null ? TitleOf(a.Service) : Loc.F("{0}, только {1}", TitleOf(a.Service), a.Host);
                Log(a.Error is null
                    ? $"{who}: «{a.PresetName ?? a.PresetId}» — {a.Score}"
                    : Loc.F("{0}: «{1}» не запустился: {2}", who, a.PresetName ?? a.PresetId, a.Error), error: a.Error is not null);
                var what = $"{Wording.ServiceTitle(a.Service)}, «{a.PresetName ?? a.PresetId}»";
                Subline = a.Error is null
                    ? Loc.F("{0}: {1}. Попыток: {2}.", what, a.Score, _attempts)
                    : Loc.F("{0}: движок не запустился. Попыток: {1}.", what, _attempts);
                break;
            case "status":
                var status = ControlProtocol.FromData<StatusReport>(data);
                if (status.Message is { } msg && msg != _lastServiceMessage)
                {
                    _lastServiceMessage = msg;
                    Log(msg, error: msg.StartsWith("не ") || msg.Contains("не удалось") || msg.Contains("не запустил"));
                    if (Notable(msg) is { } title) Notice?.Invoke(title, Loc.Msg(msg));
                }
                // сторож мог сам перезапустить движок или переподобрать: показываем, но не перебиваем идущую операцию окна
                if (!IsRunning) ApplyStatus(status);
                else UpdateProtection(status);
                break;
        }
    }

    private void OnServiceLost()
    {
        _client = null;
        ProtectVisible = false;
        ModeNote = Loc.T("Связь со службой Тропы потеряна: она остановлена или перезапускается. Доступна только диагностика.");
        CommandManager.InvalidateRequerySuggested();
    }

    private void ApplyStatus(StatusReport s)
    {
        _status = s;
        UpdateProtection(s);
        if (s.Targets.Length == 0) return;

        if (Services.Sum(x => x.Targets.Count) != s.Targets.Length)
        {
            Services.Clear();
            Build(s.Targets.Select(t => (t.Service, t.Name, t.Host)));
        }
        _last = s.Targets;
        foreach (var t in s.Targets) ApplyTarget(t);
        foreach (var svc in Services)
            svc.Refresh(s.Services.FirstOrDefault(r => r.Key == svc.Key) ?? new ServiceReport { Key = svc.Key }, PresetName);

        Summarize(s.NetworkDown, s.Protecting);
        if (s.LastCheckAt is { } at) CheckedAt = Loc.F("Проверено в {0:HH:mm}", at.LocalDateTime);
        if (s.Message is { } m && m.StartsWith("не удалось")) Subline = m;
    }

    private void UpdateProtection(StatusReport s)
    {
        _protecting = s.Protecting;
        UpdateSettings(s);
        if (!_tgBusy) ApplyTelegram(s.Telegram);
        RaiseDns();
        RaiseWeek(s);
        SyncExcluded(s);
        RaiseHome();
        Raise(nameof(ProtectLabel));
        ProtectVisible = s.Profile.Count > 0;
        TuneVisible = true;
        ModeNote = _serviceOutdated ? Loc.T("Служба Тропы из прошлой версии: она обновится при подборе обхода.")
            : s.State == ControllerState.Tuning ? Loc.T("Служба подбирает обход.") : s.Protecting
            ? Loc.F("Защита включена (winws {0}). Если что-то сломается, служба подберёт заново.", s.EngineVersion)
            : s.Profile.Count > 0 ? Loc.T("Защита выключена.") : "";
    }

    private void Build(IEnumerable<(string Service, string Name, string Host)> targets)
    {
        // контрольные в конец: они фон, а не предмет проверки
        foreach (var g in targets.GroupBy(t => t.Service).OrderBy(g => g.Key == "control"))
            Services.Add(new ServiceVm(g.Key, g.Select(t => new TargetVm(t.Service, t.Name, t.Host))));
    }

    private void ApplyTarget(TargetReport r)
    {
        foreach (var s in Services)
        {
            var t = s.Targets.FirstOrDefault(x => x.Matches(r));
            if (t is null) continue;
            t.Apply(r);
            s.Refresh();
            return;
        }
    }

    private void Summarize(bool networkDown, bool protecting)
    {
        RaiseButtons();
        var services = Services.Where(s => !s.IsControl).ToList();
        var broken = services.Where(s => s.Health == Health.Blocked).ToList();
        var slow = services.Where(s => s.Health == Health.Slow).ToList();

        if (networkDown)
        {
            Headline = Loc.T("Нет подключения к интернету");
            Subline = Loc.T("Контрольные сайты тоже не открываются, так что дело не в блокировке. Проверьте сеть и запустите проверку снова.");
            return;
        }
        var partial = services.Where(s => s.Health == Health.Partial).ToList();
        if (broken.Count == 0 && slow.Count == 0)
        {
            Headline = Loc.T("Всё открывается");
            if (partial.Count > 0)
            {
                Subline = Loc.F("У {0} не открывается второстепенное ", Wording.JoinAnd(partial.Select(s => s.Title).ToList())) +
                          $"({string.Join(", ", partial.Select(s => s.MinorText))}). " +
                          Loc.T("На работу это не влияет, переподбирать из-за этого Тропа не будет.");
                return;
            }
            var bypassed = services.Where(s => s.Report?.ActivePreset is not null).Select(s => s.Title).ToList();
            Subline = protecting && bypassed.Count > 0
                ? Loc.F("{0} {1} через настройку DPI-обработки.", Wording.JoinAnd(bypassed), (bypassed.Count == 1 ? Loc.T("работает") : Loc.T("работают")))
                : Loc.F("{0} работают без обхода.", Wording.JoinAnd(services.Select(s => s.Title).ToList()));
            return;
        }
        if (broken.Count == 0)
        {
            Headline = slow.Count == 1 ? Loc.F("{0} работает медленно", slow[0].Title) : Loc.F("{0} работают медленно", Wording.JoinAnd(slow.Select(s => s.Title).ToList()));
            Subline = Loc.T("Похоже на замедление со стороны провайдера.");
            return;
        }

        var list = Wording.JoinAnd(broken.Select(s => s.Title).ToList());
        var one = broken.Count == 1;
        Headline = one ? Loc.F("{0} не открывается", list) : Loc.F("{0} не открываются", list);
        if (protecting && broken.Any(s => s.Report?.ActivePreset is not null))
        {
            Subline = Loc.T("Обход включён, но провайдер, похоже, поменял фильтр. Служба заметит это и подберёт стратегию заново, или нажмите «Подобрать обход».");
            return;
        }
        Subline = broken.All(s => s.SniFiltered)
            ? Loc.F("Провайдер узнаёт {0} по имени сайта и глушит соединение, а сами серверы доступны. ", (one ? Loc.T("его") : Loc.T("их"))) +
              Loc.T("Нажмите «Подобрать обход».")
            : broken.All(s => s.Report?.Plan == PlanStatus.NotTreatable)
                ? Loc.T("Серверы заблокированы по адресу. Настройка DPI-обработки тут не поможет, нужен VPN или прокси.")
                : Loc.T("Нажмите на сервис, чтобы увидеть, на каком этапе рвётся соединение.");
    }

    private PresetCatalog Presets()
    {
        try { return _presets ??= PresetCatalog.Load(_presetsPath); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            return _presets = new PresetCatalog { Presets = [] };
        }
    }

    private string PresetName(string id) => Presets().Presets.FirstOrDefault(p => p.Id == id)?.Name is { } name ? Loc.T(name) : id;

    private async Task CopyAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tropa, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(Headline);
        if (_status is { } st)
            sb.AppendLine(Loc.F("служба: защита {0}, профиль: {1}", (st.Protecting ? Loc.T("включена") : Loc.T("выключена")), string.Join(", ", st.Profile.Select(kv => $"{kv.Key}={kv.Value}"))));
        foreach (var r in _last)
        {
            var where = r.FailedAt is { } stage ? $"[{stage}] {r.Failure}" : r.HttpStatus is { } code ? $"HTTP {code}" : "";
            sb.AppendLine(Loc.F("{0,-12} {1,-18} {2,-26} {3} мс", r.Verdict, r.Name, where, r.ElapsedMs));
            if (r.Detail is not null) sb.AppendLine($"             {r.Detail}");
        }
        foreach (var s in Services.Where(s => s.Report?.Candidates.Length > 0))
            sb.AppendLine(Loc.F("план {0}: {1}", s.Key, string.Join(", ", s.Report!.Candidates.Select(c => c.Id))));

        try
        {
            Clipboard.SetText(sb.ToString());
            CopyLabel = Loc.T("Скопировано");
        }
        catch (Exception)
        {
            CopyLabel = Loc.T("Буфер обмена занят");
        }
        await Task.Delay(2000);
        CopyLabel = Loc.T("Копировать отчёт");
    }
}
