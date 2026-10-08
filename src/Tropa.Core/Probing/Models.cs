using System.Net;

namespace Tropa.Core.Probing;

/// <summary>Что проверяем: хост, порт и путь, плюс критерии «работает нормально».</summary>
public sealed record ProbeTarget
{
    public required string Name { get; init; }
    public required string Host { get; init; }
    public int Port { get; init; } = 443;
    public string Path { get; init; } = "/";

    /// <summary>Сколько байт тела нужно получить. 0 = достаточно HTTP-заголовков.
    /// Для детекта «заморозки 16 КБ» ставь больше 32 КБ.</summary>
    public int MinBytes { get; init; }

    /// <summary>Ниже этой скорости (кбит/с) результат считается замедлением.</summary>
    public double? MinKbps { get; init; }

    /// <summary>Контрольный хост, который заведомо не блокируется.
    /// Если упали все контрольные, значит нет интернета, а не блокировка.</summary>
    public bool IsControl { get; init; }
}

public enum ProbeStage { Dns, Tcp, Tls, Http, Transfer }

public enum Verdict
{
    Ok,
    /// <summary>Работает, но медленно.</summary>
    Throttled,
    /// <summary>Похоже на вмешательство DPI или блокировку.</summary>
    Blocked,
    /// <summary>Нельзя сказать: нет сети, контрольные хосты тоже лежат.</summary>
    Inconclusive,
}

public enum Failure
{
    None,
    DnsFailed,
    /// <summary>DNS вернул 0.0.0.0, 127.x, приватный адрес и т.п.</summary>
    DnsBogus,
    TcpTimeout,
    TcpRefused,
    TcpReset,
    TcpUnreachable,
    /// <summary>RST или обрыв сразу после ClientHello: классический SNI-фильтр.</summary>
    TlsReset,
    /// <summary>ClientHello ушёл, ответа нет: дроп пакетов по SNI.</summary>
    TlsTimeout,
    /// <summary>Сертификат не тот: заглушка провайдера или MITM.</summary>
    TlsCertInvalid,
    /// <summary>451 или редирект на страницу блокировки.</summary>
    HttpBlockPage,
    HttpNoResponse,
    /// <summary>Данные шли и встали. В окне ~16 КБ это почерк ТСПУ.</summary>
    TransferStalled,
    TransferReset,
    Slow,
    Error,
}

public sealed record StageTiming(ProbeStage Stage, TimeSpan Duration);

public sealed record ProbeResult
{
    public required ProbeTarget Target { get; init; }
    public required Verdict Verdict { get; init; }
    public Failure Failure { get; init; }
    public ProbeStage? FailedAt { get; init; }
    public IPAddress? Address { get; init; }
    public int? HttpStatus { get; init; }
    public long BodyBytes { get; init; }
    public double? Kbps { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<StageTiming> Timings { get; init; } = [];

    public bool IsOk => Verdict == Verdict.Ok;
}
