using System.Text.Json;
using Through.Core.Probing;

namespace Through.Core.Tuning;

public sealed record ServiceProfile
{
    /// <summary>Основная стратегия сервиса; null — ни одна не взяла весь сервис, работают только уточнения.</summary>
    public string? PresetId { get; init; }
    public DateTimeOffset TunedAt { get; init; }
    /// <summary>Что было сломано без обхода: при деградации Watchdog сравнит с новым диагнозом.</summary>
    public Failure[] Diagnosis { get; init; } = [];
    /// <summary>false, если пресет лишь улучшил, но не починил сервис целиком.</summary>
    public bool Complete { get; init; }

    /// <summary>Хост → свой пресет для хостов, которые основная стратегия не взяла.</summary>
    public Dictionary<string, string> Overrides { get; init; } = [];
}

/// <summary>Выбранные стратегии по сервисам (profile.json).</summary>
public sealed record TuningProfile
{
    public Dictionary<string, ServiceProfile> Services { get; init; } = [];

    /// <summary>Группа сопутствующих профилей → выбранный вариант (подбор голоса).</summary>
    public Dictionary<string, string> Companions { get; init; } = [];

    /// <summary>Записывает сервисы, для которых нашёлся пресет. Остальные записи не трогает:
    /// неудачный подбор не должен стирать прежнюю рабочую стратегию.</summary>
    public TuningProfile Apply(TuneReport report, DateTimeOffset now)
    {
        var services = new Dictionary<string, ServiceProfile>(Services);
        foreach (var s in report.Services)
        {
            if (s.Best is null && s.Overrides.Count == 0) continue;
            services[s.Service] = new ServiceProfile
            {
                PresetId = s.Best?.Preset.Id,
                TunedAt = now,
                Diagnosis = s.Plan.Diagnosis.Where(r => !r.IsOk).Select(r => r.Failure).Distinct().ToArray(),
                Complete = s.FinalScore is { IsPerfect: true },
                Overrides = s.Overrides.ToDictionary(o => o.Host, o => o.Preset.Id),
            };
        }
        return this with { Services = services };
    }

    public static TuningProfile Load(string path) =>
        File.Exists(path) ? ThroughJson.Load<TuningProfile>(path) : new TuningProfile();

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, ThroughJson.Options));
        File.Move(tmp, path, overwrite: true); // не оставить полузаписанный профиль при сбое
    }
}
