using Tropa.Core.Probing;

namespace Tropa.Core.Tuning;

public enum PlanStatus
{
    /// <summary>Работает без обхода.</summary>
    Ok,
    NeedsTuning,
    /// <summary>Блок по IP или TLS глушат на адресе: winws тут не поможет, нужен туннель.</summary>
    NotTreatable,
    /// <summary>Сломано, но ни один пресет не заявлен как лекарство от этого диагноза.</summary>
    NoCandidates,
}

/// <summary>Что Tuner собирается делать с сервисом по итогам диагноза.</summary>
public sealed record ServicePlan(
    string Service,
    IReadOnlyList<ProbeResult> Diagnosis,
    PlanStatus Status,
    IReadOnlyList<StrategyPreset> Candidates);

public sealed record TuneAttempt
{
    public required string Service { get; init; }
    public required StrategyPreset Preset { get; init; }

    /// <summary>Попытка для одного упрямого хоста (уточнение), а не для всего сервиса.</summary>
    public string? Host { get; init; }

    public TuneScore Score { get; init; }
    /// <summary>Худший из прогонов по каждой цели сервиса.</summary>
    public IReadOnlyList<ProbeResult> Results { get; init; } = [];
    /// <summary>Движок не стартовал.</summary>
    public string? Error { get; init; }
}

public enum TuneOutcome { AlreadyOk, Fixed, Improved, NoFix, NotTreatable, NoCandidates }

/// <summary>Уточнение: этот хост обходится своей стратегией, остальной сервис — основной.
/// В winws профиль уточнения стоит раньше основного, а winws берёт первый подходящий.</summary>
public sealed record HostOverride(string Host, StrategyPreset Preset);

public sealed record ServiceTune
{
    public required ServicePlan Plan { get; init; }
    public IReadOnlyList<TuneAttempt> Attempts { get; init; } = [];
    /// <summary>Лучшая попытка, если она лучше, чем без обхода.</summary>
    public TuneAttempt? Best { get; init; }

    /// <summary>Отдельные стратегии для хостов, которые основная не взяла.</summary>
    public IReadOnlyList<HostOverride> Overrides { get; init; } = [];

    /// <summary>Итог всей связки (основная + уточнения), если уточнения есть.</summary>
    public TuneScore? CombinedScore { get; init; }

    public string Service => Plan.Service;

    public TuneScore? FinalScore => CombinedScore ?? Best?.Score;

    public TuneOutcome Outcome => Plan.Status switch
    {
        PlanStatus.Ok => TuneOutcome.AlreadyOk,
        PlanStatus.NotTreatable => TuneOutcome.NotTreatable,
        PlanStatus.NoCandidates => TuneOutcome.NoCandidates,
        _ when Best is null && Overrides.Count == 0 => TuneOutcome.NoFix,
        _ => FinalScore is { IsPerfect: true } ? TuneOutcome.Fixed : TuneOutcome.Improved,
    };
}

public sealed record TuneReport
{
    public required IReadOnlyList<ProbeResult> Diagnosis { get; init; }
    public bool NetworkDown { get; init; }
    public IReadOnlyList<ServiceTune> Services { get; init; } = [];
}

/// <summary>
/// Подбирает стратегию winws под каждый сервис. Не перебирает всё подряд:
/// ставит диагноз без обхода и пробует только пресеты, которые лечат именно его,
/// от дешёвых к дорогим. На каждом пресете гоняет пробник несколько раз и берёт худший
/// результат, чтобы случайно удачный прогон не выдал нерабочую стратегию за рабочую.
/// </summary>
public sealed class Tuner(IEngineHost engine, ProbeOptions? options = null, int runsPerPreset = 2)
{
    private readonly ProbeRunner _diagnose = new(new ServiceProber(options));

    // на попытках проверка нейтральным SNI не нужна: диагноз уже есть, а время на провале она удваивает
    private readonly ProbeRunner _attempt =
        new(new ServiceProber((options ?? ProbeOptions.Default) with { NeutralSni = null }));

    private IReadOnlyList<EngineLaunch> _background = [];

    /// <summary>
    /// Диагноз без обхода, затем подбор по каждому сервису. Движок перед вызовом должен быть остановлен.
    /// background — стратегии других сервисов: они работают и во время диагноза, и в каждой попытке,
    /// чтобы переподбор одного сервиса не рвал остальные.
    /// </summary>
    public async Task<TuneReport> TuneAsync(
        TargetCatalog targets,
        PresetCatalog presets,
        IEnumerable<string>? only = null,
        CancellationToken ct = default,
        IProgress<TuneAttempt>? progress = null,
        IReadOnlyList<EngineLaunch>? background = null)
    {
        _background = background ?? [];
        var selected = only?.ToArray();
        IReadOnlyList<ProbeResult> diagnosis;
        if (_background.Count > 0)
        {
            await using var bg = await engine.StartAsync(_background, ct);
            diagnosis = await _diagnose.RunAsync(targets.Select(selected), ct);
        }
        else
        {
            diagnosis = await _diagnose.RunAsync(targets.Select(selected), ct);
        }
        if (ProbeRunner.IsNetworkDown(diagnosis))
            return new TuneReport { Diagnosis = diagnosis, NetworkDown = true };

        var services = new List<ServiceTune>();
        foreach (var plan in Plan(targets, selected, diagnosis, presets))
        {
            services.Add(plan.Status == PlanStatus.NeedsTuning
                ? await TuneServiceAsync(plan, targets, progress, ct)
                : new ServiceTune { Plan = plan });
        }
        return new TuneReport { Diagnosis = diagnosis, Services = services };
    }

    /// <summary>План по всем выбранным сервисам (кроме чисто контрольных) без запуска движка.</summary>
    public static IReadOnlyList<ServicePlan> Plan(
        TargetCatalog targets,
        IEnumerable<string>? only,
        IReadOnlyList<ProbeResult> diagnosis,
        PresetCatalog presets)
    {
        var selected = only?.ToArray();
        return targets.Services
            .Where(kv => selected is null || selected.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
            .Where(kv => kv.Value.Any(t => !t.IsControl))
            .Select(kv => Plan(kv.Key,
                diagnosis.Where(r => !r.Target.IsControl && kv.Value.Contains(r.Target)).ToList(), presets))
            .ToList();
    }

    public static ServicePlan Plan(string service, IReadOnlyList<ProbeResult> diagnosis, PresetCatalog presets)
    {
        var failing = diagnosis.Where(r => !r.IsOk).ToList();
        if (failing.Count == 0)
            return new(service, diagnosis, PlanStatus.Ok, []);

        var treatable = failing.Where(r => !IsIpLevel(r)).Select(r => r.Failure).ToHashSet();
        if (treatable.Count == 0)
            return new(service, diagnosis, PlanStatus.NotTreatable, []);

        var candidates = presets.Treating(treatable, service);
        return new(service, diagnosis, candidates.Count == 0 ? PlanStatus.NoCandidates : PlanStatus.NeedsTuning, candidates);
    }

    /// <summary>Режут не по содержимому, а по адресу: десинхронизация DPI тут бессильна.</summary>
    private static bool IsIpLevel(ProbeResult r) =>
        r.Failure is Failure.TcpTimeout or Failure.TcpRefused or Failure.TcpUnreachable
        || (r.Failure is Failure.TlsReset or Failure.TlsTimeout && r.SniFiltered == false);

    private async Task<ServiceTune> TuneServiceAsync(
        ServicePlan plan, TargetCatalog targets, IProgress<TuneAttempt>? progress, CancellationToken ct)
    {
        var serviceTargets = targets.Services[plan.Service].Where(t => !t.IsControl).ToList();
        // контрольные гоняем вместе с сервисом: если сеть пропала посреди подбора, пресет не виноват
        var probeTargets = serviceTargets.Concat(targets.All.Where(t => t.IsControl)).Distinct().ToList();
        var (hosts, ports) = targets.BypassScope(plan.Service);

        var baseline = TuneScore.Of(plan.Diagnosis);
        var attempts = new List<TuneAttempt>();
        TuneAttempt? best = null;

        foreach (var preset in plan.Candidates)
        {
            var attempt = await AttemptAsync(plan.Service, null, [new EngineLaunch(preset, hosts, ports)], probeTargets, ct);
            attempts.Add(attempt);
            progress?.Report(attempt);
            if (attempt.Error is not null) continue;

            // строго лучше: при равенстве остаётся более дешёвый, он встретился раньше
            if (attempt.Score.CompareTo(best?.Score ?? baseline) > 0) best = attempt;
            if (attempt.Score.IsPerfect) break;
        }
        // с одной целью уточнять нечего: поиск по хосту повторил бы тот же перебор
        // второстепенные цели тоже стоит починить уточнением, если основная их не взяла
        if (best is { Score.IsPerfect: true } && best.Results.All(r => r.IsOk) || serviceTargets.Count < 2)
            return new ServiceTune { Plan = plan, Attempts = attempts, Best = best };

        // ни одна стратегия не взяла весь сервис: ищем свою для каждого упрямого хоста,
        // основная при этом работает на остальном (так же, как будет под защитой)
        var main = best is null ? null : new EngineLaunch(best.Preset, hosts, ports);
        var failing = serviceTargets
            .Where(t => best is null || best.Results.FirstOrDefault(r => r.Target == t) is not { IsOk: true })
            .ToList();
        var overrides = new List<HostOverride>();
        foreach (var target in failing)
        {
            if (overrides.Any(o => o.Host == target.Host)) continue;
            var probe = new List<ProbeTarget> { target };
            probe.AddRange(targets.All.Where(t => t.IsControl));
            foreach (var preset in plan.Candidates)
            {
                var launches = Launches(overrides.Append(new HostOverride(target.Host, preset)), main, ports);
                var attempt = await AttemptAsync(plan.Service, target.Host, launches, probe, ct);
                attempts.Add(attempt);
                progress?.Report(attempt);
                if (attempt.Error is null && attempt.Score.IsPerfect)
                {
                    overrides.Add(new HostOverride(target.Host, preset));
                    break;
                }
            }
        }
        if (overrides.Count == 0)
            return new ServiceTune { Plan = plan, Attempts = attempts, Best = best };

        // проверка связки целиком: уточнения не должны были сломать остальное
        var combined = await AttemptAsync(plan.Service, null, Launches(overrides, main, ports), probeTargets, ct);
        if (combined.Error is not null || combined.Score.CompareTo(best?.Score ?? baseline) <= 0)
            return new ServiceTune { Plan = plan, Attempts = attempts, Best = best };
        return new ServiceTune
        {
            Plan = plan, Attempts = attempts, Best = best,
            Overrides = overrides, CombinedScore = combined.Score,
        };
    }

    /// <summary>Уточнения первыми: winws берёт первый подходящий профиль.</summary>
    public static IReadOnlyList<EngineLaunch> Launches(IEnumerable<HostOverride> overrides, EngineLaunch? main, IReadOnlyList<int> ports)
    {
        var list = overrides.Select(o => new EngineLaunch(o.Preset, [o.Host], ports)).ToList();
        if (main is not null) list.Add(main);
        return list;
    }

    /// <summary>Попытка: launches[0] — то, что сейчас пробуем (для уточнения — стратегия хоста).</summary>
    private async Task<TuneAttempt> AttemptAsync(
        string service, string? host, IReadOnlyList<EngineLaunch> launches, List<ProbeTarget> probeTargets, CancellationToken ct)
    {
        var tried = host is null ? launches[0].Preset : launches.First(l => l.Hosts.Contains(host)).Preset;
        IEngineSession session;
        try
        {
            session = await engine.StartAsync([.. launches, .. _background], ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new TuneAttempt { Service = service, Host = host, Preset = tried, Error = ex.Message };
        }

        var runs = new List<IReadOnlyList<ProbeResult>>();
        await using (session)
        {
            for (var i = 0; i < runsPerPreset; i++)
                runs.Add(await _attempt.RunAsync(probeTargets, ct));
        }

        var worst = Enumerable.Range(0, probeTargets.Count)
            .Where(i => !probeTargets[i].IsControl)
            .Select(i => runs.Select(run => run[i]).MinBy(TuneScore.Rank)!)
            .ToList();
        return new TuneAttempt { Service = service, Host = host, Preset = tried, Score = TuneScore.Of(worst), Results = worst };
    }
}
