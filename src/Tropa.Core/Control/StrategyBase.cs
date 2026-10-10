using System.Net.Http.Json;
using System.Text.Json;
using Tropa.Core.Tuning;

namespace Tropa.Core.Control;

/// <summary>
/// Клиент общей базы стратегий (server/strategy-base). Перед подбором спрашивает, что работает у провайдера,
/// и эти стратегии пробуются первыми. После подбора, если человек разрешил, отправляет «сервис → стратегия → помогла».
/// Провайдера определяет сервер по соединению: отсюда уходят только id сервисов и стратегий.
/// </summary>
public sealed class StrategyBase(string baseUrl, HttpMessageHandler? handler = null)
{
    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    /// <summary>community.json рядом с программой: {"url": "https://…workers.dev"}. Пустой адрес — базы нет.</summary>
    public static StrategyBase? FromConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? new StrategyBase(url!) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public sealed record Recommendation(string Org, IReadOnlyDictionary<string, string[]> Services);

    /// <summary>Что работает у провайдера: сервис → стратегии от лучшей. null — база не ответила (подбор идёт как обычно).</summary>
    public async Task<Recommendation?> RecommendAsync(CancellationToken ct)
    {
        try
        {
            using var http = Client();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var r = await http.GetFromJsonAsync<StrategiesResponse>($"{BaseUrl}/v1/strategies", Json, cts.Token);
            if (r?.Services is null) return null;
            var services = r.Services.ToDictionary(kv => kv.Key,
                // хуже половины — не рекомендация, а предупреждение; такие не поднимаем
                kv => kv.Value.Where(x => x.Score > 0.5 && IsId(x.Preset)).Select(x => x.Preset).Take(5).ToArray());
            return new Recommendation(r.Org ?? "", services);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Что дал подбор: лучшая стратегия (и уточнения) — помогла, остальные попытки — нет.</summary>
    public static List<ReportItem> ItemsFrom(TuneReport report)
    {
        var items = new List<ReportItem>();
        foreach (var s in report.Services)
        {
            if (s.Best is { } best) items.Add(new ReportItem(s.Service, best.Preset.Id, best.Score.IsPerfect));
            foreach (var o in s.Overrides) items.Add(new ReportItem(s.Service, o.Preset.Id, true));
            foreach (var a in s.Attempts.Where(a => a.Host is null && a.Error is null && !a.Score.IsPerfect && a.Preset.Id != s.Best?.Preset.Id))
                items.Add(new ReportItem(s.Service, a.Preset.Id, false));
        }
        return [.. items.DistinctBy(i => (i.Service, i.Preset)).Take(30)];
    }

    public sealed record ReportItem(string Service, string Preset, bool Ok);

    public async Task<bool> ReportAsync(IReadOnlyList<ReportItem> items, string? engine, CancellationToken ct)
    {
        if (items.Count == 0) return false;
        try
        {
            using var http = Client();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var resp = await http.PostAsJsonAsync($"{BaseUrl}/v1/report", new
            {
                tropa = TropaBuild.Version,
                engine,
                results = items.Select(i => new { service = i.Service, preset = i.Preset, ok = i.Ok }),
            }, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private HttpClient Client()
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Tropa/{TropaBuild.Version}");
        return http;
    }

    private static bool IsId(string s) => s.Length is > 0 and <= 64 && s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record StrategiesResponse(int Asn, string? Org, Dictionary<string, Entry[]>? Services);
    private sealed record Entry(string Preset, int Ok, int Fail, int Users, double Score);
}
