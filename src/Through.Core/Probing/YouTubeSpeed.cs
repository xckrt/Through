using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Through.Core.Probing;

/// <summary>Замер: сколько мегабит в секунду реально качает видео YouTube (или почему не качает).</summary>
public sealed record SpeedResult
{
    public double Mbps { get; init; }
    public long Bytes { get; init; }
    public int Milliseconds { get; init; }
    public string? Host { get; init; }
    /// <summary>Не дошли до видео: YouTube не открылся или не отдал ссылку.</summary>
    public string? Error { get; init; }
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// Скорость видео YouTube так, как её видит человек: берём ссылку на поток публичного ролика у InnerTube
/// (клиент iOS отдаёт прямые адреса googlevideo без подписи) и качаем до 16 МБ, но не дольше 8 секунд.
/// Замедление ТСПУ режет именно googlevideo, поэтому меряем его, а не youtube.com.
/// </summary>
public static class YouTubeSpeed
{
    // длинные публичные ролики с высоким битрейтом: Big Buck Bunny, Blender Open Movie
    private static readonly string[] Videos = ["aqz-KE-bpKQ", "YE7VzlLtp-4", "eRsGyueVLvQ"];
    private const string Agent = "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";

    public static async Task<SpeedResult> MeasureAsync(CancellationToken ct = default, HttpMessageHandler? handler = null,
        string api = "https://www.youtube.com", long maxBytes = 16 * 1024 * 1024, TimeSpan? budget = null)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(6) }, disposeHandler: handler is null)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Agent);

        string? url = null, error = null;
        foreach (var id in Videos)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var resp = await http.PostAsJsonAsync($"{api}/youtubei/v1/player?prettyPrint=false", new
                {
                    videoId = id,
                    context = new { client = new { clientName = "IOS", clientVersion = "20.10.4", deviceMake = "Apple", deviceModel = "iPhone16,2", hl = "en" } },
                }, cts.Token);
                resp.EnsureSuccessStatusCode();
                var player = await resp.Content.ReadFromJsonAsync<Player>(Json, cts.Token);
                url = player?.StreamingData?.AdaptiveFormats?
                    .Where(f => f.Url is not null && f.MimeType?.StartsWith("video") == true)
                    .OrderByDescending(f => f.Bitrate)
                    .FirstOrDefault()?.Url;
                if (url is not null) break;
                error = player?.PlayabilityStatus?.Reason ?? "YouTube не отдал ссылку на видео";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                error = ex is TaskCanceledException ? "youtube.com не ответил за 8 секунд" : $"youtube.com: {ex.Message}";
            }
        }
        if (url is null) return new SpeedResult { Error = error, At = DateTimeOffset.Now };

        var host = new Uri(url).Host;
        var limit = budget ?? TimeSpan.FromSeconds(8);
        using var dl = CancellationTokenSource.CreateLinkedTokenSource(ct);
        dl.CancelAfter(limit + TimeSpan.FromSeconds(6)); // запас на соединение
        long bytes = 0;
        var sw = new Stopwatch();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, maxBytes - 1);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, dl.Token);
            resp.EnsureSuccessStatusCode();
            await using var body = await resp.Content.ReadAsStreamAsync(dl.Token);
            var buf = new byte[64 * 1024];
            sw.Start(); // время до первого байта не считаем: это соединение, а не скорость
            int n;
            while (bytes < maxBytes && sw.Elapsed < limit && (n = await body.ReadAsync(buf, dl.Token)) > 0) bytes += n;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (bytes == 0)
                return new SpeedResult { Host = host, Error = ex is TaskCanceledException ? $"{host} не начал отдавать видео" : $"{host}: {ex.Message}", At = DateTimeOffset.Now };
        }
        var ms = Math.Max(1, sw.ElapsedMilliseconds);
        return new SpeedResult
        {
            Mbps = Math.Round(bytes * 8 / 1000.0 / ms, 1),
            Bytes = bytes,
            Milliseconds = (int)ms,
            Host = host,
            At = DateTimeOffset.Now,
        };
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record Player(PlayabilityStatus? PlayabilityStatus, StreamingData? StreamingData);
    private sealed record PlayabilityStatus(string? Status, string? Reason);
    private sealed record StreamingData(Format[]? AdaptiveFormats);
    private sealed record Format(string? Url, string? MimeType, long Bitrate);
}
