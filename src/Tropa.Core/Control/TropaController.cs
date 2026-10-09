using Tropa.Core.Probing;
using Tropa.Core.Tuning;

namespace Tropa.Core.Control;

/// <summary>Движок, готовый к запуску: хост и версия скачанного zapret.</summary>
public sealed record EngineContext(IEngineHost Host, string Version);

public sealed record ControllerOptions
{
    public required TargetCatalog Targets { get; init; }
    public required PresetCatalog Presets { get; init; }

    /// <summary>Готовит движок: скачивает zapret, проверяет, что чужой winws не мешает. Зовётся лениво.</summary>
    public required Func<CancellationToken, Task<EngineContext>> Engine { get; init; }

    /// <summary>Где лежат profile.json и settings.json.</summary>
    public required string DataDir { get; init; }

    public ProbeOptions? Probe { get; init; }
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Сколько проверок подряд сервис должен лежать под защитой, чтобы сторож запустил переподбор:
    /// одна неудача бывает случайной.</summary>
    public int WatchdogStrikes { get; init; } = 2;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Есть ли zapret новее текущей версии: готовый движок или null. null целиком — обновлений нет.</summary>
    public Func<string, CancellationToken, Task<EngineContext?>>? EngineUpdate { get; init; }

    /// <summary>Новая версия запустилась: запомнить её, чтобы после перезапуска службы стартовать с неё.</summary>
    public Action<EngineContext>? EngineAccepted { get; init; }

    public TimeSpan UpdateInterval { get; init; } = TimeSpan.FromHours(24);
}

public sealed record EngineUpdateResult(bool Updated, string? Version, string Message);

/// <summary>Что служба помнит между перезапусками, кроме профиля.</summary>
public sealed record ServiceSettings
{
    public bool Protect { get; init; }

    /// <summary>Сама искать и ставить новые версии zapret.</summary>
    public bool AutoUpdate { get; init; } = true;
}

/// <summary>
/// Логика службы без Windows и без канала: проверка, подбор, защита (winws с профилем)
/// и сторож, который чинит упавший движок и переподбирает деградировавшие сервисы.
/// Тяжёлые операции идут строго по одной; вторая получает отказ, а не встаёт в очередь.
/// </summary>
public sealed class TropaController(ControllerOptions o) : IAsyncDisposable
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly ProbeRunner _runner = new(new ServiceProber(o.Probe));
    private readonly Dictionary<string, int> _strikes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _profilePath = Path.Combine(o.DataDir, "profile.json");
    private readonly string _appsPath = Path.Combine(o.DataDir, "apps.json");

    // каталоги = встроенные + приложения пользователя (apps.json); меняются при добавлении приложения
    private TargetCatalog _catalog = o.Targets;
    private PresetCatalog _presetCatalog = o.Presets;
    private List<AppDefinition> _apps = [];
    private readonly string _settingsPath = Path.Combine(o.DataDir, "settings.json");

    private TuningProfile _profile = new();
    private ServiceSettings _settings = new();
    private EngineContext? _engine;
    private IEngineSession? _session;
    private ControllerState _state;
    private DateTimeOffset? _lastCheckAt;
    private bool _networkDown;
    private TargetReport[] _targets = [];
    private ServiceReport[] _services = [];
    private string? _message;
    private DateTimeOffset? _lastUpdateCheck;

    /// <summary>Событие и его данные: "status" (StatusReport), "probe" (TargetReport), "attempt" (AttemptReport).</summary>
    public event Action<string, object>? Notify;

    public StatusReport Status() => new()
    {
        State = _state,
        Protecting = _session is { HasExited: false },
        ProtectionWanted = _settings.Protect,
        EngineVersion = _engine?.Version,
        Profile = _profile.Services.ToDictionary(kv => kv.Key, kv => kv.Value.PresetId ?? kv.Value.Overrides.Values.FirstOrDefault() ?? ""),
        LastCheckAt = _lastCheckAt,
        NetworkDown = _networkDown,
        Targets = _targets,
        Services = _services,
        Build = TropaBuild.Id,
        AutoUpdate = _settings.AutoUpdate,
        LastUpdateCheck = _lastUpdateCheck,
        Message = _message,
    };

    /// <summary>Старт службы: поднять защиту, если она была включена до перезагрузки.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(o.DataDir);
        _profile = TuningProfile.Load(_profilePath);
        try
        {
            _apps = UserApps.Load(_appsPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _message = $"apps.json не прочитался, свои приложения пропущены: {ex.Message}";
        }
        RebuildCatalogs();
        _settings = File.Exists(_settingsPath) ? TropaJson.Load<ServiceSettings>(_settingsPath) : new ServiceSettings();
        if (!_settings.Protect || !HasAnythingToProtect()) return;

        await Exclusive(async () =>
        {
            try
            {
                _message = "защита включена после запуска службы";
                await StartEngineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _message = $"не удалось включить защиту: {ex.Message}";
            }
            return 0;
        }, ct);
        Changed();
    }

    public Task<StatusReport> CheckAsync(CancellationToken ct = default) =>
        Exclusive(() => CheckCoreAsync(ct), ct, ControllerState.Checking);

    public Task<TuneSummary> TuneAsync(string[]? services = null, CancellationToken ct = default) =>
        Exclusive(() => TuneCoreAsync(services, ct), ct, ControllerState.Tuning);

    public Task<StatusReport> SetProtectionAsync(bool on, CancellationToken ct = default) => Exclusive(async () =>
    {
        if (on)
        {
            if (!HasAnythingToProtect())
                throw new InvalidOperationException("стратегий ещё нет: сначала запустите подбор");
            _message = "защита включена"; // до запуска: предупреждение о профилях без него не затрётся
            if (_session is not { HasExited: false }) await StartEngineAsync(ct);
        }
        else
        {
            await StopEngineAsync();
            _message = "защита выключена";
        }
        SaveSettings(_settings with { Protect = on });
        return Status();
    }, ct);

    public IReadOnlyList<AppDefinition> Apps() => _apps;

    /// <summary>Добавить или заменить своё приложение. Встроенные сервисы перезаписать нельзя.</summary>
    public Task<IReadOnlyList<AppDefinition>> AddAppAsync(AppDefinition app, CancellationToken ct = default) => Exclusive(async () =>
    {
        var a = UserApps.Normalize(app);
        if (o.Targets.Services.ContainsKey(a.Key))
            throw new InvalidOperationException($"«{a.Key}» уже есть среди встроенных сервисов");
        if (_apps.Count >= UserApps.MaxApps && _apps.All(x => x.Key != a.Key))
            throw new InvalidOperationException($"не больше {UserApps.MaxApps} своих приложений");

        _apps = [.. _apps.Where(x => x.Key != a.Key), a];
        UserApps.Save(_appsPath, _apps);
        RebuildCatalogs();
        _message = $"добавлено приложение «{a.Title}»";
        await RestartEngineIfRunningAsync(ct);
        return (IReadOnlyList<AppDefinition>)_apps;
    }, ct);

    public Task<IReadOnlyList<AppDefinition>> RemoveAppAsync(string key, CancellationToken ct = default) => Exclusive(async () =>
    {
        var app = _apps.FirstOrDefault(x => x.Key == key)
                  ?? throw new InvalidOperationException($"своего приложения «{key}» нет");
        _apps = [.. _apps.Where(x => x.Key != key)];
        UserApps.Save(_appsPath, _apps);
        if (_profile.Services.Remove(key)) _profile.Save(_profilePath);
        RebuildCatalogs();
        // из последней проверки убираем и сам сервис, и его цели
        _services = [.. _services.Where(x => x.Key != key)];
        _targets = [.. _targets.Where(t => t.Service != key)];
        _message = $"приложение «{app.Title}» удалено";
        await RestartEngineIfRunningAsync(ct);
        return (IReadOnlyList<AppDefinition>)_apps;
    }, ct);

    /// <summary>Каталог поменялся: перезапустить winws с новым составом, если защита включена.</summary>
    private async Task RestartEngineIfRunningAsync(CancellationToken ct)
    {
        if (_session is not { HasExited: false }) return;
        await StopEngineAsync();
        if (!HasAnythingToProtect())
        {
            _message += "; защищать больше нечего, winws остановлен";
            return;
        }
        await StartEngineAsync(ct);
    }

    /// <summary>Проверить и поставить новую версию zapret. Если она не запустилась, вернуться на прежнюю.</summary>
    public Task<EngineUpdateResult> UpdateEngineAsync(CancellationToken ct = default) =>
        Exclusive(() => UpdateCoreAsync(ct), ct);

    public Task<StatusReport> SetAutoUpdateAsync(bool on, CancellationToken ct = default) => Exclusive(() =>
    {
        SaveSettings(_settings with { AutoUpdate = on });
        _message = on ? "zapret будет обновляться сам" : "автообновление zapret выключено";
        return Task.FromResult(Status());
    }, ct);

    private async Task<EngineUpdateResult> UpdateCoreAsync(CancellationToken ct)
    {
        if (o.EngineUpdate is null) return new(false, _engine?.Version, "обновление недоступно");
        var old = await EnsureEngineAsync(ct);
        _lastUpdateCheck = o.Clock();
        var next = await o.EngineUpdate(old.Version, ct);
        if (next is null)
        {
            _message = $"zapret {old.Version} — последняя версия";
            return new(false, old.Version, _message);
        }

        var protecting = _session is { HasExited: false };
        await StopEngineAsync();
        _engine = next;
        if (protecting)
        {
            try
            {
                await StartEngineAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // новая версия не взлетела с нашими профилями: остаёмся на проверенной
                _engine = old;
                await StartEngineAsync(ct);
                _message = $"zapret {next.Version} не запустился ({ex.Message}), остаюсь на {old.Version}";
                return new(false, old.Version, _message);
            }
        }
        o.EngineAccepted?.Invoke(next);
        _message = $"zapret обновлён: {old.Version} → {next.Version}";
        return new(true, next.Version, _message);
    }

    /// <summary>Раз в UpdateInterval, если включено и служба не занята.</summary>
    public async Task MaybeAutoUpdateAsync(CancellationToken ct = default)
    {
        if (!_settings.AutoUpdate || o.EngineUpdate is null || _engine is null) return;
        if (_lastUpdateCheck is { } last && o.Clock() - last < o.UpdateInterval) return;
        if (!await _busy.WaitAsync(0, ct)) return;
        try
        {
            await UpdateCoreAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastUpdateCheck = o.Clock();
            _message = $"не удалось проверить обновления zapret: {ex.Message}";
        }
        finally
        {
            _busy.Release();
            Changed();
        }
    }

    /// <summary>Сторож: бесконечный цикл до отмены.</summary>
    public async Task RunWatchdogAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(o.WatchdogInterval, ct);
                await WatchdogTickAsync(ct);
                await MaybeAutoUpdateAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _message = $"сторож: {ex.Message}";
                Changed();
            }
        }
    }

    /// <summary>
    /// Один обход сторожа. Если занято (идёт подбор или проверка по запросу человека), пропускает ход.
    /// Упавший движок перезапускает, сервисы, лежащие под защитой WatchdogStrikes раз подряд, переподбирает.
    /// </summary>
    public async Task WatchdogTickAsync(CancellationToken ct = default)
    {
        if (!_settings.Protect || !HasAnythingToProtect()) return;
        if (!await _busy.WaitAsync(0, ct)) return;
        try
        {
            if (_session is not { HasExited: false })
            {
                await StopEngineAsync();
                await StartEngineAsync(ct);
                _message = "движок остановился сам и был перезапущен";
                Changed();
            }

            _state = ControllerState.Checking;
            await CheckCoreAsync(ct);
            if (_networkDown) return; // без сети деградацию не засчитываем

            var degraded = new List<string>();
            foreach (var service in _profile.Services.Keys)
            {
                // второстепенные проверки (обновления клиента) не повод рвать работающий обход переподбором
                var broken = _targets.Any(t => t.Service.Equals(service, StringComparison.OrdinalIgnoreCase) && !t.IsOk && !t.Optional);
                _strikes[service] = broken ? _strikes.GetValueOrDefault(service) + 1 : 0;
                if (_strikes[service] >= o.WatchdogStrikes) degraded.Add(service);
            }
            if (degraded.Count == 0) return;

            _message = $"перестало работать: {string.Join(", ", degraded)}, подбираю заново";
            _state = ControllerState.Tuning;
            Changed();
            await TuneCoreAsync(degraded.ToArray(), ct);
            foreach (var s in degraded) _strikes[s] = 0;
        }
        finally
        {
            _state = ControllerState.Idle;
            _busy.Release();
            Changed();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopEngineAsync();
        _busy.Dispose();
    }

    // ---------- внутреннее: вызывается под _busy

    private async Task<StatusReport> CheckCoreAsync(CancellationToken ct)
    {
        var serviceOf = ServiceOf();
        var progress = new SyncProgress<ProbeResult>(r =>
            Notify?.Invoke("probe", TargetReport.From(serviceOf.GetValueOrDefault(r.Target, ""), r)));

        var results = await _runner.RunAsync(_catalog.All, ct, progress);
        _networkDown = ProbeRunner.IsNetworkDown(results);
        _targets = results.Select(r => TargetReport.From(serviceOf.GetValueOrDefault(r.Target, ""), r)).ToArray();

        var protecting = _session is { HasExited: false };
        var plans = _networkDown ? [] : Tuner.Plan(_catalog, null, results, _presetCatalog);
        _services = _catalog.Services
            .Where(kv => kv.Value.Any(t => !t.IsControl))
            .Select(kv =>
            {
                var plan = plans.FirstOrDefault(p => p.Service == kv.Key);
                return new ServiceReport
                {
                    Key = kv.Key,
                    Title = _apps.FirstOrDefault(a => a.Key == kv.Key)?.Title,
                    Plan = plan?.Status ?? PlanStatus.Ok,
                    Candidates = plan?.Candidates.Select(c => new CandidateReport(c.Id, c.Name)).ToArray() ?? [],
                    ActivePreset = protecting && _profile.Services.TryGetValue(kv.Key, out var p) ? p.PresetId ?? "" : null,
                    Overrides = protecting && _profile.Services.TryGetValue(kv.Key, out var po) ? po.Overrides : [],
                };
            })
            .ToArray();
        _lastCheckAt = o.Clock();
        Changed();
        return Status();
    }

    private async Task<TuneSummary> TuneCoreAsync(string[]? services, CancellationToken ct)
    {
        // остальные сервисы при переподборе одного продолжают работать: их стратегии идут фоном
        List<EngineLaunch> background = [];
        if (services is not null)
        {
            var (others, extras) = BuildLaunches(new HashSet<string>(services, StringComparer.OrdinalIgnoreCase));
            background = [.. others, .. extras];
        }
        // диагноз подбираемых сервисов ставится без их обхода: свой же winws испортил бы картину
        await StopEngineAsync();
        try
        {
            var engine = await EnsureEngineAsync(ct);
            // три прогона, а не два: на живой сети стратегия, проскочившая дважды, потом сыпалась (dl.discordapp.net)
            var report = await new Tuner(engine.Host, o.Probe, runsPerPreset: 3).TuneAsync(_catalog, _presetCatalog, services, ct,
                new SyncProgress<TuneAttempt>(a => Notify?.Invoke("attempt", AttemptReport.From(a))), background);

            if (report.NetworkDown)
            {
                _message = "контрольные сайты недоступны: похоже, нет сети, подбор отменён";
                return new TuneSummary { NetworkDown = true };
            }

            _profile = _profile.Apply(report, o.Clock());
            _profile.Save(_profilePath);
            var fixedAny = report.Services.Any(s => s.Best is not null);
            if (fixedAny) SaveSettings(_settings with { Protect = true });

            _message = report.Services.All(s => s.Outcome is TuneOutcome.AlreadyOk or TuneOutcome.Fixed)
                ? "подбор закончен, всё работает"
                : "подбор закончен, но не всё удалось починить";
            return new TuneSummary
            {
                Services = report.Services.Select(s => new TuneServiceSummary(s.Service, s.Outcome, s.Best?.Preset.Id)).ToArray(),
            };
        }
        finally
        {
            // и после успеха, и после сбоя возвращаем защиту, если она нужна: человек не должен остаться без обхода
            if (_settings.Protect && HasAnythingToProtect() && _session is null)
            {
                try
                {
                    await StartEngineAsync(ct);
                    await CheckCoreAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _message = $"не удалось включить защиту: {ex.Message}";
                }
            }
        }
    }

    private async Task StartEngineAsync(CancellationToken ct)
    {
        var (launches, companions) = BuildLaunches(null);
        if (launches.Count + companions.Count == 0)
            throw new InvalidOperationException("в профиле нет стратегий, известных этой версии presets.json");

        var engine = await EnsureEngineAsync(ct);
        if (companions.Count > 0)
        {
            try
            {
                _session = await engine.Host.StartAsync([.. launches, .. companions], ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && launches.Count > 0)
            {
                // QUIC, голос и игры — дополнение: если winws не принял их, подобранный обход важнее
                _message = $"дополнительные профили ({string.Join(", ", companions.Select(c => c.Preset.Id))}) не запустились, защита без них: {ex.Message}";
            }
        }
        _session = await engine.Host.StartAsync(launches, ct);
    }

    /// <summary>Что отдать winws по профилю: стратегии (уточнения раньше основной) и сопутствующие профили.
    /// exclude — сервисы, которые сейчас подбираются: их стратегий здесь не будет.</summary>
    private (List<EngineLaunch> Launches, List<EngineLaunch> Companions) BuildLaunches(ISet<string>? exclude)
    {
        var launches = new List<EngineLaunch>();
        var companions = new List<EngineLaunch>();
        var withStrategy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string service, StrategyPreset preset)
        {
            var (hosts, ports) = _catalog.BypassScope(service);
            if (hosts.Count == 0) return;
            launches.Add(new EngineLaunch(preset, hosts, ports));
            withStrategy.Add(service);
        }

        StrategyPreset? Find(string? id) => id is null ? null : _presetCatalog.Presets.FirstOrDefault(p => p.Id == id);

        foreach (var (service, entry) in _profile.Services)
        {
            if (!_catalog.Services.ContainsKey(service) || exclude?.Contains(service) == true) continue;
            // уточнения раньше основной: winws берёт первый подходящий профиль
            var (_, ports) = _catalog.BypassScope(service);
            foreach (var (host, presetId) in entry.Overrides)
                if (Find(presetId) is { } own)
                {
                    launches.Add(new EngineLaunch(own, [host], ports));
                    withStrategy.Add(service);
                }
            if (Find(entry.PresetId) is { } preset) Add(service, preset);
        }

        // «обходить всегда»: приложения нет в профиле (проверка проходит), берём основную стратегию
        foreach (var app in _apps.Where(a => a.Always && !withStrategy.Contains(a.Key) && exclude?.Contains(a.Key) != true))
            if (_presetCatalog.Treating(new HashSet<Failure> { Failure.TlsReset, Failure.TlsTimeout }, app.Key).FirstOrDefault() is { } preset)
                Add(app.Key, preset);

        // то, что не подбирается, но нужно приложению: QUIC, голос, игровые порты
        foreach (var c in _presetCatalog.Companions.Where(c => (c.Always || withStrategy.Contains(c.Service)) && exclude?.Contains(c.Service) != true))
        {
            var (hosts, ports) = _catalog.BypassScope(c.Service);
            companions.Add(new EngineLaunch(new StrategyPreset { Id = c.Id, Treats = [], Args = c.Args }, hosts, ports));
        }
        return (launches, companions);
    }

    private bool HasAnythingToProtect() =>
        _profile.Services.Count > 0 || _apps.Any(a => a.Always || a.UdpPorts.Length > 0 || a.TcpPorts.Length > 0);

    private void RebuildCatalogs()
    {
        _catalog = UserApps.Merge(o.Targets, _apps);
        _presetCatalog = UserApps.Merge(o.Presets, _apps);
    }

    private async Task StopEngineAsync()
    {
        if (_session is { } s)
        {
            _session = null;
            await s.DisposeAsync();
        }
    }

    private async Task<EngineContext> EnsureEngineAsync(CancellationToken ct) => _engine ??= await o.Engine(ct);

    private async Task<T> Exclusive<T>(Func<Task<T>> body, CancellationToken ct, ControllerState state = ControllerState.Idle)
    {
        if (!await _busy.WaitAsync(0, ct))
            throw new InvalidOperationException(_state == ControllerState.Tuning
                ? "идёт подбор, дождитесь окончания"
                : "служба занята, попробуйте через минуту");
        try
        {
            _state = state;
            if (state != ControllerState.Idle) Changed();
            return await body();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && state == ControllerState.Tuning)
        {
            _message = $"подбор не удался: {ex.Message}";
            throw;
        }
        finally
        {
            _state = ControllerState.Idle;
            _busy.Release();
            Changed();
        }
    }

    private Dictionary<ProbeTarget, string> ServiceOf()
    {
        var map = new Dictionary<ProbeTarget, string>();
        foreach (var (key, targets) in _catalog.Services)
            foreach (var t in targets)
                map.TryAdd(t, key);
        return map;
    }

    private void SaveSettings(ServiceSettings s)
    {
        _settings = s;
        Directory.CreateDirectory(o.DataDir);
        var tmp = _settingsPath + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(s, TropaJson.Options));
        File.Move(tmp, _settingsPath, overwrite: true);
    }

    private void Changed() => Notify?.Invoke("status", Status());

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
