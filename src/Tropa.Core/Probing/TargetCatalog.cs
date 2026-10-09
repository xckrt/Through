namespace Tropa.Core.Probing;

/// <summary>Набор целей, сгруппированных по сервисам. Хранится в JSON, правится без пересборки.</summary>
public sealed record TargetCatalog
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
}
