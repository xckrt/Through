using Tropa.Core.Control;
using Tropa.Core.Probing;

namespace Tropa.Tray;

/// <summary>Диагнозы пробника простыми словами: для человека, а не для отладки.</summary>
internal static class Wording
{
    public static string ServiceTitle(string key) => key.ToLowerInvariant() switch
    {
        "youtube" => "YouTube",
        "discord" => "Discord",
        "cloudflare" => "Cloudflare",
        "control" => "Контрольные сайты",
        _ => key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : key,
    };

    public static string Explain(TargetReport r) => r.Verdict switch
    {
        Verdict.Ok => "работает",
        Verdict.Inconclusive => "нет сети, проверить нельзя",
        Verdict.Throttled => "работает, но медленно",
        _ => r.Failure switch
        {
            Failure.DnsBogus => "DNS подменяет адрес",
            Failure.DnsFailed => "имя сайта не находится",
            Failure.TcpTimeout or Failure.TcpRefused or Failure.TcpUnreachable or Failure.TcpReset => "адрес недоступен",
            Failure.TlsTimeout or Failure.TlsReset => r.SniFiltered switch
            {
                true => r.Failure == Failure.TlsReset ? "обрывают по имени сайта" : "глушат по имени сайта",
                false => "адрес заблокирован целиком",
                null => "защищённое соединение не устанавливается",
            },
            Failure.TlsCertInvalid => "подменён сертификат",
            Failure.HttpBlockPage => "открывается страница блокировки",
            Failure.HttpNoResponse => "сервер не отвечает на запрос",
            Failure.TransferStalled => r.Detail?.Contains("16 КБ") == true ? "загрузка встаёт на 16 КБ" : "загрузка встаёт",
            Failure.TransferReset => "загрузку обрывают",
            _ => "не работает",
        },
    };

    public static string Metric(TargetReport r)
    {
        if (r.IsOk && r.MinBytes > 0 && r.Kbps is { } k)
            return k >= 1000 ? $"{k / 1000:F1} Мбит/с" : $"{k:F0} кбит/с";
        return r.ElapsedMs < 1000 ? $"{r.ElapsedMs} мс" : $"{r.ElapsedMs / 1000.0:F1} с";
    }

    /// <summary>«YouTube», «YouTube и Discord», «YouTube, Discord и Cloudflare».</summary>
    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " и " + items[^1],
    };

    public static string Plural(int n, string one, string few, string many)
    {
        var d = n % 100;
        if (d is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }
}
