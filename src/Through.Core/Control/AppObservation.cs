using System.Text.RegularExpressions;

namespace Through.Core.Control;

/// <summary>Куда ходила программа, пока служба её слушала: адрес, порт, протокол и имя, если оно было в DNS.</summary>
public sealed record ObservedEndpoint(string Address, int Port, string Protocol, string? Host);

public sealed record ObservedTraffic
{
    public required string Process { get; init; }
    /// <summary>Имена, которые программа спрашивала у DNS.</summary>
    public string[] Hosts { get; init; } = [];
    public ObservedEndpoint[] Endpoints { get; init; } = [];
    /// <summary>Сколько секунд слушали и были ли вообще процессы с таким именем.</summary>
    public int Seconds { get; init; }
    public bool ProcessFound { get; init; }
}

/// <summary>
/// Из подсмотренного трафика — заготовка приложения: домены (регистрируемые, без системного шума),
/// хосты для проверки и порты (не 80/443) диапазонами. Человек правит её в той же форме, что и вручную.
/// </summary>
public static partial class AppSuggester
{
    // фоновый шум Windows, античитов-телеметрии и CDN-проверок, который ходит из любого процесса
    private static readonly string[] Noise =
    [
        "microsoft.com", "windows.com", "windowsupdate.com", "msftconnecttest.com", "msftncsi.com", "live.com",
        "office.com", "office.net", "bing.com", "msn.com", "digicert.com", "verisign.com", "sectigo.com",
        "globalsign.com", "letsencrypt.org", "lencr.org", "identrust.com", "pki.goog", "gstatic.com",
        "google-analytics.com", "googletagmanager.com", "doubleclick.net", "app-measurement.com",
        "local", "localhost", "arpa",
    ];

    public static bool IsNoise(string domain) => Noise.Any(n => domain == n || domain.EndsWith("." + n, StringComparison.Ordinal));

    /// <summary>Имя процесса «как видит Windows» → допустимый: только имя файла, без путей.</summary>
    public static string NormalizeProcess(string name)
    {
        var n = (name ?? "").Trim();
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        if (!ProcessRe().IsMatch(n)) throw new InvalidDataException("имя программы: буквы, цифры, точка, дефис и подчёркивание, до 64 символов");
        return n;
    }

    public static AppDefinition Suggest(string title, ObservedTraffic t)
    {
        var hosts = t.Hosts.Concat(t.Endpoints.Select(e => e.Host).OfType<string>())
            .Select(h => h.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(h => h.Contains('.') && !System.Net.IPAddress.TryParse(h, out _))
            .ToList();

        // домен, к которому обращались чаще, — первым
        var domains = hosts.Select(SiteCheck.RegistrableDomain)
            .Where(d => !IsNoise(d))
            .GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key)
            .Take(60).ToArray();

        var checkHosts = hosts.Where(h => !IsNoise(SiteCheck.RegistrableDomain(h)))
            .GroupBy(h => h).OrderByDescending(g => g.Count()).Select(g => g.Key)
            .Take(3).ToArray();

        static bool Web(int p) => p is 80 or 443 or 53 or 853;
        var udp = Ranges(t.Endpoints.Where(e => e.Protocol == "udp" && !Web(e.Port)).Select(e => e.Port));
        var tcp = Ranges(t.Endpoints.Where(e => e.Protocol == "tcp" && !Web(e.Port)).Select(e => e.Port));

        var key = KeyFrom(title);
        return new AppDefinition
        {
            Key = key,
            Title = title.Length > 40 ? title[..40] : title,
            Domains = domains,
            CheckHosts = checkHosts,
            UdpPorts = udp.Take(10).ToArray(),
            TcpPorts = tcp.Take(10).ToArray(),
            // игра, которая ходит по UDP, скорее тормозит, чем не открывается: обход держим всегда
            Always = udp.Count > 0,
        };
    }

    /// <summary>Порты → «7777» и «27015-27030»: соседние (в пределах 10) склеиваются в диапазон.</summary>
    public static List<string> Ranges(IEnumerable<int> ports)
    {
        var sorted = ports.Where(p => p is > 0 and < 65536).Distinct().Order().ToList();
        var result = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] - end <= 10) end = sorted[++i];
            result.Add(start == end ? $"{start}" : $"{start}-{end}");
            i++;
        }
        return result;
    }

    private static string KeyFrom(string title)
    {
        var latin = new string(title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (latin.Contains("--")) latin = latin.Replace("--", "-");
        if (latin.Length == 0) latin = "app";
        return latin.Length > 32 ? latin[..32].Trim('-') : latin;
    }

    [GeneratedRegex(@"^[\p{L}\p{N}._ -]{1,64}$")]
    private static partial Regex ProcessRe();
}
