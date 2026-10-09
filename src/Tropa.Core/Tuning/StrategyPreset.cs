using Tropa.Core.Probing;

namespace Tropa.Core.Tuning;

/// <summary>
/// Стратегия winws: аргументы и диагнозы, которые она лечит.
/// В Args допустимы подстановки {hostlist}, {ports} и {engineDir}, их раскрывает EngineLaunch.
/// </summary>
public sealed record StrategyPreset
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public required Failure[] Treats { get; init; }

    /// <summary>Чем дешевле, тем раньше пробуем: меньше побочных эффектов и нагрузки.</summary>
    public int Cost { get; init; }

    public required string[] Args { get; init; }

    /// <summary>Только для этих сервисов (у Google свои стратегии). null — для любого.</summary>
    public string[]? Services { get; init; }

    public bool AppliesTo(string service) =>
        Services is null || Services.Contains(service, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Профиль winws, который включается под защитой вместе со стратегией сервиса, но не подбирается:
/// то, что пробник не умеет мерить (QUIC, голос по UDP). Hostlist у него тот же, что у сервиса.
/// </summary>
public sealed record CompanionProfile
{
    public required string Id { get; init; }
    public required string Service { get; init; }
    public required string[] Args { get; init; }

    /// <summary>Включать под защитой всегда, даже если сервиса нет в профиле (игровые порты).</summary>
    public bool Always { get; init; }
}

/// <summary>Набор пресетов. Хранится в JSON, обновляется без релиза программы.</summary>
public sealed record PresetCatalog
{
    public required List<StrategyPreset> Presets { get; init; }

    public List<CompanionProfile> Companions { get; init; } = [];

    /// <summary>Пресеты, которые лечат хотя бы один из диагнозов, от дешёвых к дорогим.
    /// При равной стоимости сохраняется порядок файла.</summary>
    public IReadOnlyList<StrategyPreset> Treating(IReadOnlySet<Failure> failures, string? service = null) =>
        Presets.Where(p => p.Treats.Any(failures.Contains) && (service is null || p.AppliesTo(service)))
               .OrderBy(p => p.Cost).ToList();

    public static PresetCatalog Load(string path)
    {
        var catalog = TropaJson.Load<PresetCatalog>(path);
        var dup = catalog.Presets.GroupBy(p => p.Id).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null)
            throw new InvalidDataException($"{path}: пресет {dup.Key} описан дважды");
        var empty = catalog.Presets.FirstOrDefault(p => p.Args.Length == 0 || p.Treats.Length == 0);
        if (empty is not null)
            throw new InvalidDataException($"{path}: у пресета {empty.Id} пустые args или treats");
        return catalog;
    }
}
