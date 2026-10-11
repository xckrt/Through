namespace Tropa.Core.Probing;

/// <summary>Набор целей, сгруппированных по сервисам. Хранится в JSON, правится без пересборки.</summary>
public sealed partial record TargetCatalog
{
    public required Dictionary<string, List<ProbeTarget>> Services { get; init; }

    /// <summary>
    /// Домены сервиса для обхода (hostlist winws), шире целей проверки: приложение ходит на десятки
    /// хостов, а проверяем мы три. winws обходит домен вместе со всеми поддоменами.
    /// </summary>
    public Dictionary<string, List<string>> Domains { get; init; } = [];

    /// <summary>Дополнительные TCP-порты сервиса для обхода (у Discord медиа на 2053, 8443 и др.).</summary>
    public Dictionary<string, List<int>> Ports { get; init; } = [];

    /// <summary>Что отдать winws для сервиса: домены из Domains плюс хосты и порты целей проверки.</summary>
    public (IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports) BypassScope(string service)
    {
        var own = Services.TryGetValue(service, out var t) ? t.Where(x => !x.IsControl).ToList() : [];
        var hosts = Domains.GetValueOrDefault(service, []).Concat(own.Select(x => x.Host))
            .Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)
            .Distinct().ToList();
        var ports = own.Select(x => x.Port).Concat(Ports.GetValueOrDefault(service, [])).Distinct().Order().ToList();
        return (hosts, ports);
    }

    public IEnumerable<ProbeTarget> All => Services.Values.SelectMany(v => v);

    public IEnumerable<ProbeTarget> Select(IEnumerable<string>? services) =>
        services is null
            ? All
            : Services.Where(kv => services.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                      .SelectMany(kv => kv.Value)
                      .Concat(All.Where(t => t.IsControl))
                      .Distinct();

    public static TargetCatalog Load(string path) => TropaJson.Load<TargetCatalog>(path);

    /// <summary>
    /// Добавить домены к известным сервисам (обновляемый список с сервера). Только добавление и только
    /// к уже существующим сервисам: порты, цели проверки и стратегии winws не трогаются. Так подмена
    /// списка на сервере может лишь расширить обход на лишние домены, но не выполнить чужие аргументы.
    /// </summary>
    public TargetCatalog WithExtraDomains(IReadOnlyDictionary<string, IReadOnlyList<string>> extra)
    {
        if (extra.Count == 0) return this;
        var domains = Domains.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        foreach (var (service, list) in extra)
        {
            if (!Services.ContainsKey(service)) continue; // только известные сервисы
            var target = domains.TryGetValue(service, out var d) ? d : domains[service] = [];
            foreach (var dom in list)
                if (!target.Contains(dom)) target.Add(dom);
        }
        return this with { Domains = domains };
    }

    /// <summary>Строгая проверка домена из недоверенного источника: обычное имя, без мусора и без аргументов.</summary>
    public static bool IsValidDomain(string d) =>
        d.Length is > 3 and <= 253 && DomainRe().IsMatch(d);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$")]
    private static partial System.Text.RegularExpressions.Regex DomainRe();
}
