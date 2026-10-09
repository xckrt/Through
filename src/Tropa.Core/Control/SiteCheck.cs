using System.Globalization;
using System.Text.RegularExpressions;
using Tropa.Core.Probing;

namespace Tropa.Core.Control;

/// <summary>Кто виноват, что сайт не открывается, и можно ли это исправить настройкой DPI-обработки.</summary>
public enum SiteVerdict
{
    /// <summary>Открывается.</summary>
    Works,
    /// <summary>Режут по имени сайта, замораживают поток, подсовывают заглушку: обход должен помочь.</summary>
    Fixable,
    /// <summary>Работает, но медленно: похоже на замедление, обход может помочь.</summary>
    Slow,
    /// <summary>Глушат адрес целиком или сайт лежит: настройка DPI тут бессильна.</summary>
    AddressBlocked,
    /// <summary>DNS подменяет адрес: нужен другой DNS, а не обход.</summary>
    DnsSpoofed,
    /// <summary>Имя не находится: опечатка, сайта нет или DNS режет.</summary>
    NotFound,
    /// <summary>Отказывает сам сайт (403/451 от сервера): часто это блокировка по стране с его стороны.</summary>
    SiteRefuses,
    /// <summary>Нет сети или сбой проверки.</summary>
    Unknown,
}

public sealed record SiteCheckResult(string Host, string Domain, ProbeResult Probe, SiteVerdict Verdict);

/// <summary>
/// «Почему не открывается?»: человек вставляет адрес, Тропа гоняет его по этапам и говорит, кто виноват.
/// Если виноват провайдер и это лечится, домен можно сразу отдать в обход.
/// </summary>
public static partial class SiteCheck
{
    // зоны, где регистрируют на третьем уровне: bbc.co.uk, site.com.ru
    private static readonly HashSet<string> SecondLevel = new(StringComparer.Ordinal)
    {
        "co.uk", "org.uk", "ac.uk", "com.ru", "net.ru", "org.ru", "pp.ru", "msk.ru", "spb.ru",
        "com.ua", "org.ua", "co.jp", "ne.jp", "com.au", "net.au", "com.br", "com.tr", "co.kr", "com.cn",
        "co.in", "com.kz", "org.kz", "com.by",
    };

    /// <summary>Из того, что вставил человек, — хост: схема, путь, порт и «www.» в любом регистре не мешают.
    /// Кириллические домены переводятся в punycode. null — не похоже на адрес сайта.</summary>
    public static string? NormalizeHost(string input)
    {
        var s = input.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(['/', '?', '#', ':', ' ']);
        if (cut >= 0) s = s[..cut];
        s = s.Trim().TrimEnd('.').ToLowerInvariant();
        if (s.Length == 0) return null;
        try { s = new IdnMapping().GetAscii(s); }
        catch (ArgumentException) { return null; }
        return HostRe().IsMatch(s) ? s : null;
    }

    /// <summary>Что отдать в обход: регистрируемый домен (поддомены winws обходит сам).</summary>
    public static string RegistrableDomain(string host)
    {
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        var lastTwo = $"{labels[^2]}.{labels[^1]}";
        return SecondLevel.Contains(lastTwo) && labels.Length >= 3 ? $"{labels[^3]}.{lastTwo}" : lastTwo;
    }

    /// <summary>Цель для пробника: главная страница, с запасом 64 КБ, чтобы поймать заморозку на 16 КБ.</summary>
    public static ProbeTarget Target(string host) => new() { Name = host, Host = host, Path = "/", MinBytes = 64 * 1024 };

    public static SiteVerdict Classify(ProbeResult r)
    {
        if (r.Verdict == Verdict.Ok)
            return r.HttpStatus is 403 or 451 ? SiteVerdict.SiteRefuses : SiteVerdict.Works;
        if (r.Verdict == Verdict.Throttled) return SiteVerdict.Slow;
        if (r.Verdict == Verdict.Inconclusive) return SiteVerdict.Unknown;
        return r.Failure switch
        {
            Failure.TlsReset or Failure.TlsTimeout => r.SniFiltered == false ? SiteVerdict.AddressBlocked : SiteVerdict.Fixable,
            Failure.TcpTimeout or Failure.TcpRefused or Failure.TcpUnreachable or Failure.TcpReset => SiteVerdict.AddressBlocked,
            Failure.DnsBogus => SiteVerdict.DnsSpoofed,
            Failure.DnsFailed => SiteVerdict.NotFound,
            Failure.HttpBlockPage or Failure.TlsCertInvalid or Failure.TransferStalled or Failure.TransferReset
                or Failure.HttpNoResponse => SiteVerdict.Fixable,
            Failure.Slow => SiteVerdict.Slow,
            _ => SiteVerdict.Unknown,
        };
    }

    public static async Task<SiteCheckResult> RunAsync(ServiceProber prober, string host, CancellationToken ct = default)
    {
        var probe = await prober.ProbeAsync(Target(host), ct);
        return new SiteCheckResult(host, RegistrableDomain(host), probe, Classify(probe));
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex HostRe();
}
