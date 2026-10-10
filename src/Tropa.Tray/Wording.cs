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
        "control" => Loc.T("Контрольные сайты"),
        _ => key.Length > 0 ? char.ToUpperInvariant(key[0]) + key[1..] : key,
    };

    public static string Explain(TargetReport r) => r.Verdict switch
    {
        Verdict.Ok => Loc.T("работает"),
        Verdict.Inconclusive => Loc.T("нет сети, проверить нельзя"),
        Verdict.Throttled => Loc.T("работает, но медленно"),
        _ => r.Failure switch
        {
            Failure.DnsBogus => Loc.T("DNS подменяет адрес"),
            Failure.DnsFailed => Loc.T("имя сайта не находится"),
            Failure.TcpTimeout or Failure.TcpRefused or Failure.TcpUnreachable or Failure.TcpReset => Loc.T("адрес недоступен"),
            Failure.TlsTimeout or Failure.TlsReset => r.SniFiltered switch
            {
                true => r.Failure == Failure.TlsReset ? Loc.T("обрывают по имени сайта") : Loc.T("глушат по имени сайта"),
                false => Loc.T("адрес заблокирован целиком"),
                null => Loc.T("защищённое соединение не устанавливается"),
            },
            Failure.TlsCertInvalid => Loc.T("подменён сертификат"),
            Failure.HttpBlockPage => Loc.T("открывается страница блокировки"),
            Failure.HttpNoResponse => Loc.T("сервер не отвечает на запрос"),
            Failure.TransferStalled => r.Detail?.Contains("16 КБ") == true ? Loc.T("загрузка встаёт на 16 КБ") : Loc.T("загрузка встаёт"),
            Failure.TransferReset => Loc.T("загрузку обрывают"),
            Failure.UdpBlocked => Loc.T("голос не проходит"),
            _ => Loc.T("не работает"),
        },
    };

    public static string Metric(TargetReport r)
    {
        if (r.IsOk && r.MinBytes > 0 && r.Kbps is { } k)
            return k >= 1000 ? Loc.F("{0:F1} Мбит/с", k / 1000) : Loc.F("{0:F0} кбит/с", k);
        return r.ElapsedMs < 1000 ? Loc.F("{0} мс", r.ElapsedMs) : Loc.F("{0:F1} с", r.ElapsedMs / 1000.0);
    }

    /// <summary>«YouTube», «YouTube и Discord», «YouTube, Discord и Cloudflare».</summary>
    public static string JoinAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + Loc.T(" и ") + items[^1],
    };

    public static string Plural(int n, string one, string few, string many) => Loc.Plural(n, one, few, many);

    public static string RuPlural(int n, string one, string few, string many)
    {
        var d = n % 100;
        if (d is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }
}
