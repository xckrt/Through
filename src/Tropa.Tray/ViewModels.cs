using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
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

public enum Health { Pending, Ok, Slow, Blocked, Unknown }

public sealed class TargetVm(ProbeTarget target) : Observable
{
    private TrailState _state;
    private int _stopAt;
    private string _verdict = "";
    private string _metric = "";

    public ProbeTarget Target => target;
    public ProbeResult? Result { get; private set; }
    public string Name => target.Name;

    public TrailState State { get => _state; private set => Set(ref _state, value); }
    public int StopAt { get => _stopAt; private set => Set(ref _stopAt, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string Metric { get => _metric; private set => Set(ref _metric, value); }

    public void Reset()
    {
        Result = null;
        State = TrailState.Pending;
        Verdict = "";
        Metric = "";
    }

    public void Apply(ProbeResult r)
    {
        Result = r;
        StopAt = (int)(r.FailedAt ?? ProbeStage.Dns);
        State = r.Verdict switch
        {
            Core.Probing.Verdict.Ok => TrailState.Ok,
            Core.Probing.Verdict.Throttled => TrailState.Throttled,
            Core.Probing.Verdict.Blocked => TrailState.Broken,
            _ => TrailState.Unknown,
        };
        Verdict = Wording.Explain(r);
        Metric = Wording.Metric(r);
    }
}

public sealed class ServiceVm : Observable
{
    private bool _expanded;
    private Health _health;
    private string _summary = "";
    private string? _planHeading;

    public ServiceVm(string key, IEnumerable<ProbeTarget> targets)
    {
        Key = key;
        Title = Wording.ServiceTitle(key);
        Targets = new(targets.Select(t => new TargetVm(t)));
        IsControl = Targets.All(t => t.Target.IsControl);
    }

    public string Key { get; }
    public string Title { get; }
    public bool IsControl { get; }
    public ObservableCollection<TargetVm> Targets { get; }
    public ObservableCollection<string> PlanItems { get; } = [];
    public ObservableCollection<string> Technical { get; } = [];
    public ServicePlan? Plan { get; private set; }

    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    public Health Health { get => _health; private set => Set(ref _health, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    public string? PlanHeading
    {
        get => _planHeading;
        private set { if (Set(ref _planHeading, value)) Raise(nameof(HasPlanHeading)); }
    }

    public bool HasPlanHeading => PlanHeading is not null;

    public void Reset()
    {
        foreach (var t in Targets) t.Reset();
        Plan = null;
        PlanHeading = null;
        PlanItems.Clear();
        Technical.Clear();
        Refresh();
    }

    /// <summary>Пересчитать сводку по целям; plan появляется, когда пришли все результаты.</summary>
    public void Refresh(ServicePlan? plan = null)
    {
        var results = Targets.Select(t => t.Result).ToList();
        Health = results.Any(r => r is null) ? Health.Pending
            : results.Any(r => r!.Verdict == Verdict.Blocked) ? Health.Blocked
            : results.Any(r => r!.Verdict == Verdict.Inconclusive) ? Health.Unknown
            : results.Any(r => r!.Verdict == Verdict.Throttled) ? Health.Slow
            : Health.Ok;

        var blocked = results.Where(r => r?.Verdict == Verdict.Blocked).ToList();
        Summary = Health switch
        {
            Health.Pending => "проверяю",
            Health.Ok => IsControl ? "сеть в порядке" : "работает",
            Health.Slow => "работает медленно",
            Health.Unknown => "нет сети",
            _ when blocked.All(r => r!.SniFiltered == true) => "режут по имени сайта",
            _ when plan?.Status == PlanStatus.NotTreatable => "заблокирован по адресу",
            _ => "не работает",
        };

        if (plan is null) return;
        Plan = plan;
        PlanItems.Clear();
        PlanHeading = plan.Status switch
        {
            PlanStatus.NeedsTuning =>
                $"Подборщик попробует {plan.Candidates.Count} {Wording.Plural(plan.Candidates.Count, "стратегию", "стратегии", "стратегий")}, от простых к сложным:",
            PlanStatus.NotTreatable =>
                "Соединение блокируют по адресу сервера. Настройка DPI-обработки тут не поможет, нужен VPN или прокси.",
            PlanStatus.NoCandidates => "Для такой блокировки пока нет готовых стратегий.",
            _ => null,
        };
        foreach (var p in plan.Candidates)
            PlanItems.Add(p.Name is null ? p.Id : $"{p.Name} ({p.Id})");

        Technical.Clear();
        foreach (var t in Targets)
            if (t.Result is { } r)
                Technical.Add($"{r.Target.Host}{(r.Address is null ? "" : $" ({r.Address})")}: {r.Detail ?? "без замечаний"}");
    }
}

public sealed class MainViewModel : Observable
{
    private readonly string _targetsPath;
    private readonly string _presetsPath;
    private readonly ProbeRunner _runner;
    private CancellationTokenSource? _cts;
    private TargetCatalog? _catalog;
    private IReadOnlyList<ProbeResult> _last = [];

    private string _headline = "Проверяю сервисы";
    private string _subline = "Обычно это занимает до 15 секунд.";
    private bool _running;
    private bool _canTune;
    private string _checkLabel = "Проверить";
    private string _copyLabel = "Копировать отчёт";
    private string _checkedAt = "";

    /// <param name="options">Настройки пробника; по умолчанию системный DNS и проверка сертификатов.</param>
    /// <param name="dataDir">Где лежат targets.json и presets.json; по умолчанию рядом с программой.</param>
    public MainViewModel(ProbeOptions? options = null, string? dataDir = null)
    {
        dataDir ??= AppContext.BaseDirectory;
        _targetsPath = Path.Combine(dataDir, "targets.json");
        _presetsPath = Path.Combine(dataDir, "presets.json");
        _runner = new ProbeRunner(new ServiceProber(options));
        CheckCommand = new RelayCommand(CheckAsync, () => !IsRunning);
        CopyCommand = new RelayCommand(CopyAsync, () => !IsRunning && _last.Count > 0);
    }

    public ObservableCollection<ServiceVm> Services { get; } = [];
    public RelayCommand CheckCommand { get; }
    public RelayCommand CopyCommand { get; }

    public string Headline { get => _headline; private set => Set(ref _headline, value); }
    public string Subline { get => _subline; private set => Set(ref _subline, value); }
    public bool IsRunning { get => _running; private set { Set(ref _running, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanTune { get => _canTune; private set => Set(ref _canTune, value); }
    public string CheckLabel { get => _checkLabel; private set => Set(ref _checkLabel, value); }
    public string CopyLabel { get => _copyLabel; private set => Set(ref _copyLabel, value); }
    public string CheckedAt { get => _checkedAt; private set => Set(ref _checkedAt, value); }

    public void Cancel() => _cts?.Cancel();

    private async Task CheckAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        CanTune = false;
        var label = CheckLabel;
        CheckLabel = "Проверяю…";
        Headline = "Проверяю сервисы";
        Subline = "Обычно это занимает до 15 секунд.";
        _cts = new CancellationTokenSource();
        try
        {
            if (_catalog is null)
            {
                _catalog = TargetCatalog.Load(_targetsPath);
                // контрольные в конец: они фон, а не предмет проверки
                foreach (var (key, targets) in _catalog.Services.OrderBy(kv => kv.Value.All(t => t.IsControl)))
                    Services.Add(new ServiceVm(key, targets));
            }
            foreach (var s in Services) s.Reset();

            var byTarget = Services.SelectMany(s => s.Targets.Select(t => (s, t))).ToDictionary(x => x.t.Target);
            var progress = new Progress<ProbeResult>(r =>
            {
                if (!byTarget.TryGetValue(r.Target, out var x)) return;
                x.t.Apply(r);
                x.s.Refresh();
            });

            _last = await _runner.RunAsync(_catalog.All, _cts.Token, progress);
            foreach (var r in _last)
                if (byTarget.TryGetValue(r.Target, out var x)) x.t.Apply(r);

            var networkDown = ProbeRunner.IsNetworkDown(_last);
            var plans = networkDown ? [] : Tuner.Plan(_catalog, null, _last, PresetCatalog.Load(_presetsPath));
            foreach (var s in Services)
                s.Refresh(plans.FirstOrDefault(p => p.Service == s.Key) ?? new ServicePlan(s.Key, [], PlanStatus.Ok, []));

            Summarize(networkDown);
            CanTune = plans.Any(p => p.Status == PlanStatus.NeedsTuning);
            CheckedAt = $"Проверено в {DateTime.Now:HH:mm}";
            label = "Проверить снова";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Headline = "Проверка не запустилась";
            Subline = $"{ex.Message} Проверьте, что targets.json и presets.json лежат рядом с программой.";
        }
        finally
        {
            CheckLabel = label;
            IsRunning = false;
        }
    }

    private void Summarize(bool networkDown)
    {
        var services = Services.Where(s => !s.IsControl).ToList();
        var broken = services.Where(s => s.Health == Health.Blocked).ToList();
        var slow = services.Where(s => s.Health == Health.Slow).ToList();

        if (networkDown)
        {
            Headline = "Нет подключения к интернету";
            Subline = "Контрольные сайты тоже не открываются, так что дело не в блокировке. Проверьте сеть и запустите проверку снова.";
            return;
        }
        if (broken.Count == 0 && slow.Count == 0)
        {
            Headline = "Всё открывается";
            Subline = $"{Wording.JoinAnd(services.Select(s => s.Title).ToList())} работают без обхода.";
            return;
        }
        if (broken.Count == 0)
        {
            Headline = slow.Count == 1 ? $"{slow[0].Title} работает медленно" : $"{Wording.JoinAnd(slow.Select(s => s.Title).ToList())} работают медленно";
            Subline = "Похоже на замедление со стороны провайдера.";
            return;
        }

        var names = Wording.JoinAnd(broken.Select(s => s.Title).ToList());
        Headline = broken.Count == 1 ? $"{names} не открывается" : $"{names} не открываются";

        var plans = broken.Select(s => s.Plan).ToList();
        var bySni = broken.All(s => s.Summary == "режут по имени сайта");
        var one = broken.Count == 1;
        Subline = bySni
            ? $"Провайдер узнаёт {(one ? "его" : "их")} по имени сайта и глушит соединение, а сами серверы доступны. Такое обычно лечится настройкой DPI-обработки."
            : plans.All(p => p?.Status == PlanStatus.NotTreatable)
                ? "Серверы заблокированы по адресу. Настройка DPI-обработки тут не поможет, нужен VPN или прокси."
                : "Нажмите на сервис, чтобы увидеть, на каком этапе рвётся соединение.";
    }

    private async Task CopyAsync()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Tropa, {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine(Headline);
        foreach (var r in _last)
        {
            var where = r.FailedAt is { } st ? $"[{st}] {r.Failure}" : r.HttpStatus is { } code ? $"HTTP {code}" : "";
            sb.AppendLine($"{r.Verdict,-12} {r.Target.Name,-18} {where,-26} {(int)r.Elapsed.TotalMilliseconds} мс");
            if (r.Detail is not null) sb.AppendLine($"             {r.Detail}");
        }
        foreach (var s in Services.Where(s => s.Plan?.Candidates.Count > 0))
            sb.AppendLine($"план {s.Key}: {string.Join(", ", s.Plan!.Candidates.Select(c => c.Id))}");

        try
        {
            Clipboard.SetText(sb.ToString());
            CopyLabel = "Скопировано";
        }
        catch (Exception)
        {
            CopyLabel = "Буфер обмена занят";
        }
        await Task.Delay(2000);
        CopyLabel = "Копировать отчёт";
    }
}
