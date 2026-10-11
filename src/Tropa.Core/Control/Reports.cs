using System.Text.Json.Serialization;
using Tropa.Core.Probing;
using Tropa.Core.Telegram;
using Tropa.Core.Tuning;

namespace Tropa.Core.Control;

/// <summary>Результат пробника в виде, пригодном для канала службы и для окна.</summary>
public sealed record TargetReport
{
    public required string Service { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public bool IsControl { get; init; }
    public bool Optional { get; init; }
    public string? Role { get; init; }
    public bool Voice { get; init; }
    public Verdict Verdict { get; init; }
    public Failure Failure { get; init; }
    public ProbeStage? FailedAt { get; init; }
    public bool? SniFiltered { get; init; }
    public int? HttpStatus { get; init; }
    public long BodyBytes { get; init; }
    public int MinBytes { get; init; }
    public double? Kbps { get; init; }
    public int ElapsedMs { get; init; }
    public string? Address { get; init; }
    public string? Detail { get; init; }

    [JsonIgnore] public bool IsOk => Verdict == Verdict.Ok;

    public static TargetReport From(string service, ProbeResult r) => new()
    {
        Service = service,
        Name = r.Target.Name,
        Host = r.Target.Host,
        IsControl = r.Target.IsControl,
        Optional = r.Target.Optional,
        Role = r.Target.Role,
        Voice = r.Target.Voice,
        Verdict = r.Verdict,
        Failure = r.Failure,
        FailedAt = r.FailedAt,
        SniFiltered = r.SniFiltered,
        HttpStatus = r.HttpStatus,
        BodyBytes = r.BodyBytes,
        MinBytes = r.Target.MinBytes,
        Kbps = r.Kbps,
        ElapsedMs = (int)r.Elapsed.TotalMilliseconds,
        Address = r.Address?.ToString(),
        Detail = r.Detail,
    };
}

public sealed record CandidateReport(string Id, string? Name);

public sealed record ServiceReport
{
    public required string Key { get; init; }
    /// <summary>Название своего приложения; у встроенных сервисов пусто, окно знает их само.</summary>
    public string? Title { get; init; }
    public PlanStatus Plan { get; init; }
    public CandidateReport[] Candidates { get; init; } = [];
    /// <summary>Пресет из профиля, если защита включена («» — только уточнения).</summary>
    public string? ActivePreset { get; init; }

    /// <summary>Хост → свой пресет для упрямых хостов.</summary>
    public Dictionary<string, string> Overrides { get; init; } = [];
}

public enum ControllerState { Idle, Checking, Tuning }

public sealed record StatusReport
{
    public ControllerState State { get; init; }
    /// <summary>winws сейчас работает.</summary>
    public bool Protecting { get; init; }
    /// <summary>Пользователь хочет защиту (переживает перезагрузку).</summary>
    public bool ProtectionWanted { get; init; }
    public string? EngineVersion { get; init; }
    /// <summary>Сервис → id пресета из profile.json.</summary>
    public Dictionary<string, string> Profile { get; init; } = [];
    public DateTimeOffset? LastCheckAt { get; init; }
    public bool NetworkDown { get; init; }
    public TargetReport[] Targets { get; init; } = [];
    public ServiceReport[] Services { get; init; } = [];
    public bool AutoUpdate { get; init; }
    public DateTimeOffset? LastUpdateCheck { get; init; }

    /// <summary>Отпечаток сборки службы (TropaBuild.Id). У старых служб его нет.</summary>
    public string? Build { get; init; }

    /// <summary>Последнее, что служба хочет сказать человеку: ошибка или что она сделала сама.</summary>
    public string? Message { get; init; }

    /// <summary>Прокси Telegram. У служб старее 1.1 его нет.</summary>
    public TelegramReport? Telegram { get; init; }

    public DnsReport? Dns { get; init; }

    /// <summary>Имя текущей сети: у каждой сети свой подобранный обход.</summary>
    public string? Network { get; init; }

    /// <summary>Скорость видео YouTube без обхода и с ним (последние замеры).</summary>
    public SpeedReport? Speed { get; init; }

    public HomeProxyReport? Home { get; init; }

    public bool ShareCrashes { get; init; }

    /// <summary>Игровой фильтр: широкий UDP-диапазон для игр и голоса.</summary>
    public bool GameFilter { get; init; }

    /// <summary>«Не трогать»: сайты вне обхода.</summary>
    public string[] Excluded { get; init; } = [];

    /// <summary>null — в этой сборке база не настроена (пустой community.json).</summary>
    public CommunityReport? Community { get; init; }

    public WeeklyReport? Week { get; init; }
    public int KnownNetworks { get; init; }
}

/// <summary>Общая база стратегий подключена: берём ли подсказки и делимся ли итогами.</summary>
public sealed record CommunityReport(bool Use, bool Share);

/// <summary>Что Тропа сделала за неделю: для строки на обзоре и уведомления в конце недели.</summary>
public sealed record WeekReport
{
    public DateTimeOffset Start { get; init; }
    /// <summary>Часы, пока winws держал обход.</summary>
    public double ProtectedHours { get; init; }
    /// <summary>Сколько раз служба сама переподобрала сломавшийся сервис.</summary>
    public int AutoFixes { get; init; }
    public long TelegramBytes { get; init; }
    public long TelegramConnections { get; init; }
    public long DnsQueries { get; init; }
    /// <summary>Во сколько раз YouTube быстрее с обходом (последний замер), если оба замера есть.</summary>
    public double? YoutubeTimes { get; init; }
}

public sealed record WeeklyReport(WeekReport Current, WeekReport? Previous);

public sealed record SpeedReport
{
    public Tropa.Core.Probing.SpeedResult? Without { get; init; }
    public Tropa.Core.Probing.SpeedResult? With { get; init; }
}

public sealed record HomeProxyReport
{
    public bool Enabled { get; init; }
    public bool Running { get; init; }
    public int Port { get; init; }
    /// <summary>адрес:порт в домашней сети — для ручной настройки прокси.</summary>
    public string? Address { get; init; }
    /// <summary>http://адрес:порт/proxy.pac — «Прокси → Авто» на телефоне.</summary>
    public string? PacUrl { get; init; }
    public Tropa.Core.Proxy.HomeProxyStats? Stats { get; init; }
    public string? Error { get; init; }
}

public sealed record DnsReport
{
    public bool Enabled { get; init; }
    /// <summary>Локальный сервер открыт.</summary>
    public bool Running { get; init; }
    /// <summary>Система смотрит на него (адаптеры переключены).</summary>
    public bool Active { get; init; }
    public Tropa.Core.Doh.SecureDnsStats? Stats { get; init; }
    public string? Error { get; init; }
}

public sealed record TelegramReport
{
    public bool Enabled { get; init; }

    /// <summary>Порт открыт и принимает соединения.</summary>
    public bool Running { get; init; }
    public int Port { get; init; }

    /// <summary>tg://proxy?… — открыть в Telegram, чтобы добавить прокси.</summary>
    public string? Link { get; init; }

    /// <summary>Пускаем телефоны из домашней сети.</summary>
    public bool Lan { get; init; }

    /// <summary>https://t.me/proxy?server=адрес-в-сети… — для QR-кода: камера телефона откроет его в Telegram.</summary>
    public string? LanLink { get; init; }
    public string? Error { get; init; }
    public TelegramProxyStats? Stats { get; init; }
    public TelegramDcCheck[] Checks { get; init; } = [];
    public DateTimeOffset? CheckedAt { get; init; }
}

public sealed record AttemptReport
{
    public required string Service { get; init; }
    public required string PresetId { get; init; }
    public string? PresetName { get; init; }
    /// <summary>Уточнение для одного хоста, а не попытка для всего сервиса.</summary>
    public string? Host { get; init; }
    public string Score { get; init; } = "";
    public bool Perfect { get; init; }
    public string? Error { get; init; }

    public static AttemptReport From(TuneAttempt a) => new()
    {
        Service = a.Service,
        PresetId = a.Preset.Id,
        PresetName = a.Preset.Name,
        Host = a.Host,
        Score = a.Score.ToString(),
        Perfect = a.Score.IsPerfect,
        Error = a.Error,
    };
}

public sealed record TuneServiceSummary(string Service, TuneOutcome Outcome, string? PresetId);

public sealed record TuneSummary
{
    public bool NetworkDown { get; init; }
    public TuneServiceSummary[] Services { get; init; } = [];
}
