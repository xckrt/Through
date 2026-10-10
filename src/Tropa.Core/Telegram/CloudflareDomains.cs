using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tropa.Core.Telegram;

/// <summary>
/// Домены за Cloudflare, через которые tg-ws-proxy достаёт DC, когда прямой вход недоступен
/// (kws{dc}.{домен} проксирует /apiws). Список ведёт Flowseal и хранит в слегка зашифрованном виде,
/// чтобы его не собирали в блок-листы автоматически; храним так же и раз в час обновляем из его репозитория.
/// </summary>
public sealed class CloudflareDomains
{
    public const string ListUrl = "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";

    // raw.githubusercontent.com режут у части провайдеров по DNS: тогда идём на известный адрес GitHub
    private const string GithubAddress = "185.199.109.133";

    private static readonly string[] Encoded =
    [
        "virkgj.com", "vmmzovy.com", "mkuosckvso.com", "zaewayzmplad.com", "twdmbzcm.com",
        "awzwsldi.com", "clngqrflngqin.com", "tjacxbqtj.com", "bxaxtxmrw.com", "dmohrsgmohcrwb.com",
        "vwbmtmoi.com", "khgrre.com", "ulihssf.com", "tmhqsdqmfpmk.com", "xwuwoqbm.com",
        "orgcnunpj.com", "zhkuldz.com", "zypoljnslxa.com", "efabnxaowuzs.com", "zaftuzsftqdq.com",
    ];

    private readonly object _lock = new();
    private string[] _domains;
    private readonly Dictionary<int, string> _current = [];

    public CloudflareDomains(IEnumerable<string>? domains = null)
    {
        _domains = domains?.ToArray() ?? Encoded.Select(Decode).ToArray();
    }

    public IReadOnlyList<string> All
    {
        get { lock (_lock) return _domains; }
    }

    /// <summary>Расшифровка из tg-ws-proxy: сдвиг букв назад на их число, «.com» → «.co.uk».</summary>
    public static string Decode(string s)
    {
        if (!s.EndsWith(".com", StringComparison.Ordinal)) return s;
        var p = s[..^4];
        var n = p.Count(char.IsAsciiLetter);
        var sb = new StringBuilder(p.Length + 6);
        foreach (var c in p)
        {
            if (!char.IsAsciiLetter(c)) { sb.Append(c); continue; }
            var a = char.IsAsciiLetterLower(c) ? 'a' : 'A';
            sb.Append((char)(((c - a - n) % 26 + 26) % 26 + a));
        }
        return sb.Append(".co.uk").ToString();
    }

    /// <summary>Домены для DC: сначала тот, что сработал в прошлый раз, потом остальные вперемешку.</summary>
    public List<string> For(int dc)
    {
        lock (_lock)
        {
            var list = _domains.OrderBy(_ => Random.Shared.Next()).ToList();
            if (_current.TryGetValue(dc, out var cur) && list.Remove(cur)) list.Insert(0, cur);
            return list;
        }
    }

    public void Worked(int dc, string domain)
    {
        lock (_lock) _current[dc] = domain;
    }

    public void Replace(IReadOnlyList<string> domains)
    {
        lock (_lock)
        {
            _domains = [.. domains];
            foreach (var dc in _current.Where(kv => !_domains.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                _current.Remove(dc);
        }
    }

    /// <summary>Скачать свежий список. Меньше трёх годных доменов — считаем ответ мусором и оставляем старый.</summary>
    public async Task<bool> RefreshAsync(string url, CancellationToken ct)
    {
        foreach (var pinned in new[] { false, true })
        {
            try
            {
                var text = await FetchAsync(url, pinned, ct);
                var fresh = text.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith('#'))
                    .Select(Decode)
                    .Select(d => d.ToLowerInvariant())
                    .Where(IsValidDomain)
                    .Distinct()
                    .ToArray();
                if (fresh.Length < 3) return false;
                Replace(fresh);
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }
        return false;
    }

    private static async Task<string> FetchAsync(string url, bool pinned, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) };
        if (pinned)
            handler.ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(IPAddress.Parse(GithubAddress), ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Tropa");
        return await http.GetStringAsync(url + "?" + Random.Shared.Next(), ct);
    }

    public static bool IsValidDomain(string d)
    {
        if (d.Length is 0 or > 253 || d.StartsWith('.') || d.EndsWith('.')) return false;
        var labels = d.Split('.');
        if (labels.Length < 2) return false;
        foreach (var l in labels)
            if (l.Length is 0 or > 63 || l[0] == '-' || l[^1] == '-' || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;
        var tld = labels[^1];
        return tld.Length >= 2 && tld.Any(char.IsAsciiLetter);
    }
}
