namespace Tropa.Core.Tuning;

/// <summary>Что запустить: пресет, сужённый до хостов и портов одного сервиса.</summary>
public sealed record EngineLaunch(StrategyPreset Preset, IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports)
{
    /// <summary>Сайты, которые обход не трогает никогда (банки, госуслуги, которые ломает стратегия):
    /// в профиль уходит --hostlist-exclude-domains, а из hostlist они убираются.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Хосты профиля без исключённых доменов и их поддоменов.</summary>
    public IReadOnlyList<string> EffectiveHosts =>
        Exclude.Count == 0 ? Hosts : Hosts.Where(h => !Exclude.Any(e => h == e || h.EndsWith("." + e, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Аргументы для winws: hostlist-файл с Hosts пишет сам движок.</summary>
    public IReadOnlyList<string> RenderArgs(string hostlistPath, string engineDir) =>
        Preset.Args
            .Select(a => a.Replace("{hostlist}", hostlistPath)
                          .Replace("{ports}", string.Join(',', Ports))
                          .Replace("{engineDir}", engineDir))
            .ToList();

    /// <summary>
    /// Несколько сервисов в одном winws: общие --wf-* (фильтр WinDivert) один раз с объединёнными
    /// портами, затем профиль каждого сервиса, разделённые --new.
    /// </summary>
    public static IReadOnlyList<string> Compose(
        IReadOnlyList<EngineLaunch> launches, IReadOnlyList<string> hostlistPaths, string engineDir)
    {
        var global = new List<string>();
        var ports = new Dictionary<string, List<string>>(); // --wf-tcp / --wf-udp → порты и диапазоны «a-b»
        var profiles = new List<List<string>>();

        for (var i = 0; i < launches.Count; i++)
        {
            var profile = new List<string>();
            foreach (var arg in launches[i].RenderArgs(hostlistPaths[i], engineDir))
            {
                if (!arg.StartsWith("--wf-", StringComparison.Ordinal)) { profile.Add(arg); continue; }
                var eq = arg.IndexOf('=');
                var key = eq < 0 ? arg : arg[..eq];
                if (key is "--wf-tcp" or "--wf-udp" && eq > 0)
                {
                    var list = ports.TryGetValue(key, out var l) ? l : ports[key] = [];
                    foreach (var p in arg[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!list.Contains(p)) list.Add(p);
                }
                else if (!global.Contains(arg))
                {
                    global.Add(arg);
                }
            }
            if (launches[i].Exclude.Count > 0) profile.Add("--hostlist-exclude-domains=" + string.Join(',', launches[i].Exclude));
            profiles.Add(profile);
        }

        static int Start(string p) => int.TryParse(p.Split('-')[0], out var n) ? n : int.MaxValue;
        var result = ports.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={string.Join(',', kv.Value.OrderBy(Start))}")
            .Concat(global).ToList();
        for (var i = 0; i < profiles.Count; i++)
        {
            if (i > 0) result.Add("--new");
            result.AddRange(profiles[i]);
        }
        return result;
    }
}

/// <summary>
/// Запуск и остановка движка DPI-обработки. Настоящая реализация (winws.exe) живёт
/// в Windows-проекте, в Core только контракт: Tuner тестируется с фейковым движком.
/// </summary>
public interface IEngineHost
{
    /// <summary>Один движок на все launches (у каждого свой профиль). Возвращается, когда движок
    /// готов обрабатывать трафик. Бросает, если не стартовал.</summary>
    Task<IEngineSession> StartAsync(IReadOnlyList<EngineLaunch> launches, CancellationToken ct = default);
}

/// <summary>Запущенный движок. DisposeAsync останавливает его.</summary>
public interface IEngineSession : IAsyncDisposable
{
    /// <summary>Что движок успел написать в stderr: для отчёта, если пресет не взлетел.</summary>
    string Log { get; }

    /// <summary>Движок умер сам: сторож службы перезапустит его.</summary>
    bool HasExited { get; }
}
