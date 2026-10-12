using System.Net;

namespace Through.Core.Probing;

public delegate Task<IPAddress[]> HostResolver(string host, CancellationToken ct);

public sealed record ProbeOptions
{
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Голос: сколько ждать ответа голосовых серверов на заход (заходов два).</summary>
    public TimeSpan VoiceTimeout { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Голос: свои адреса вместо списка регионов (тесты). null — список Discord.</summary>
    public IReadOnlyList<string>? VoiceAddresses { get; init; }
    public IReadOnlyList<int>? VoicePorts { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TlsTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Сколько ждать следующую порцию данных, прежде чем считать поток вставшим.</summary>
    public TimeSpan ReadIdleTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Потолок на весь пробник целиком.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Сколько IP из DNS-ответа пробовать при неудачном TCP-коннекте.</summary>
    public int MaxAddresses { get; init; } = 3;

    /// <summary>Окно (в байтах тела), где остановка потока считается «заморозкой ТСПУ».
    /// Режут на ~16 КБ TLS-трафика; тело чуть меньше из-за заголовков, отсюда запас.</summary>
    public int FreezeWindowMin { get; init; } = 8 * 1024;
    public int FreezeWindowMax { get; init; } = 32 * 1024;

    /// <summary>Не считать локальные и приватные адреса признаком подмены DNS (тесты, LAN).</summary>
    public bool AllowPrivateAddresses { get; init; }

    /// <summary>Только для тестов против самоподписанного сервера.</summary>
    public bool ValidateCertificate { get; init; } = true;

    /// <summary>SNI для повторного рукопожатия на тот же IP при провале TLS: отличает фильтр по SNI
    /// от глушения всего TLS на адресе. null = не проверять.</summary>
    public string? NeutralSni { get; init; } = "ya.ru";

    /// <summary>Подмена резолвера (тесты, DoH). По умолчанию системный DNS.</summary>
    public HostResolver? Resolver { get; init; }

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0 Safari/537.36";

    public static ProbeOptions Default { get; } = new();
}
