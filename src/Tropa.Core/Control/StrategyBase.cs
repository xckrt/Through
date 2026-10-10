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

    /// <summary>«У меня сервис сломан / работает»: под обходом (preset) или без него (то, что режет провайдер).</summary>
    public sealed record StatusItem(string Service, bool Blocked, bool Protected, string? Preset, string? Failure);

    /// <summary>У провайдера массово сломалась стратегия: сколько людей, с какого часа и что помогло другим.</summary>
    public sealed record Alert(string Service, string Preset, int Users, string? Fix);

    public async Task<IReadOnlyList<Alert>> AlertsAsync(CancellationToken ct)
    {
        try
        {
            using var http = Client();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            var r = await http.GetFromJsonAsync<AlertsResponse>($"{BaseUrl}/v1/alerts", Json, cts.Token);
            return r?.Alerts?.Where(a => IsId(a.Service) && IsId(a.Preset) && (a.Fix is null || IsId(a.Fix))).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException && !ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>Статусы из проверки: блок, под обходом ли, какая стратегия, чем режут. Только сервисы, без хостов.</summary>
    public static List<StatusItem> StatusFrom(IEnumerable<TargetReport> targets, Func<string, string?> activePreset, bool protecting)
    {
        var items = new List<StatusItem>();
        foreach (var g in targets.Where(t => !t.IsControl && !t.Optional && IsId(t.Service)).GroupBy(t => t.Service))
        {
            var bad = g.Where(t => t.Verdict == Probing.Verdict.Blocked).ToList();
            var preset = protecting ? activePreset(g.Key) : null;
            items.Add(new StatusItem(g.Key, bad.Count > 0, preset is not null, preset,
                bad.GroupBy(t => t.Failure).OrderByDescending(x => x.Count()).Select(x => x.Key.ToString()).FirstOrDefault()));
        }
        return items;
    }

    public async Task<bool> ReportAsync(IReadOnlyList<ReportItem> items, string? engine, CancellationToken ct, IReadOnlyList<StatusItem>? status = null)
    {
        if (items.Count == 0 && status is not { Count: > 0 }) return false;
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
                status = (status ?? []).Select(s => new { service = s.Service, blocked = s.Blocked, @protected = s.Protected, preset = s.Preset, failure = s.Failure }),
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

    private sealed record AlertsResponse(int Asn, string? Org, Alert[]? Alerts);
    private sealed record StrategiesResponse(int Asn, string? Org, Dictionary<string, Entry[]>? Services);
    private sealed record Entry(string Preset, int Ok, int Fail, int Users, double Score);
}
