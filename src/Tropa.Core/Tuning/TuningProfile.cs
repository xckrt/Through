using System.Text.Json;
using Tropa.Core.Probing;

namespace Tropa.Core.Tuning;

public sealed record ServiceProfile
{
    public required string PresetId { get; init; }
    public DateTimeOffset TunedAt { get; init; }
    /// <summary>Что было сломано без обхода: при деградации Watchdog сравнит с новым диагнозом.</summary>
    public Failure[] Diagnosis { get; init; } = [];
    /// <summary>false, если пресет лишь улучшил, но не починил сервис целиком.</summary>
    public bool Complete { get; init; }
}

/// <summary>Выбранные стратегии по сервисам (profile.json).</summary>
public sealed record TuningProfile
{
    public Dictionary<string, ServiceProfile> Services { get; init; } = [];

    /// <summary>Записывает сервисы, для которых нашёлся пресет. Остальные записи не трогает:
    /// неудачный подбор не должен стирать прежнюю рабочую стратегию.</summary>
    public TuningProfile Apply(TuneReport report, DateTimeOffset now)
    {
        var services = new Dictionary<string, ServiceProfile>(Services);
        foreach (var s in report.Services)
        {
            if (s.Best is not { } best) continue;
            services[s.Service] = new ServiceProfile
            {
                PresetId = best.Preset.Id,
                TunedAt = now,
                Diagnosis = s.Plan.Diagnosis.Where(r => !r.IsOk).Select(r => r.Failure).Distinct().ToArray(),
                Complete = best.Score.IsPerfect,
            };
        }
        return this with { Services = services };
    }

    public static TuningProfile Load(string path) =>
        File.Exists(path) ? TropaJson.Load<TuningProfile>(path) : new TuningProfile();

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, TropaJson.Options));
        File.Move(tmp, path, overwrite: true); // не оставить полузаписанный профиль при сбое
    }
}
