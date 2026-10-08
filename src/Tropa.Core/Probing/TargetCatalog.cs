namespace Tropa.Core.Probing;

/// <summary>Набор целей, сгруппированных по сервисам. Хранится в JSON, правится без пересборки.</summary>
public sealed record TargetCatalog
{
    public required Dictionary<string, List<ProbeTarget>> Services { get; init; }

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
