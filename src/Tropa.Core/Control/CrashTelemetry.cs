using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tropa.Core.Control;

/// <summary>Обезличенная сводка об ошибке: тип и имена методов, без сообщения, путей и данных.</summary>
public sealed record CrashSummary(string App, string Version, string Type, string[] Frames, string Os);

/// <summary>
/// Сводки об ошибках — только если человек включил это в настройках. Сообщение исключения не уходит никогда:
/// в нём бывают пути, адреса сайтов и имена. Уходит тип и до 8 кадров стека в виде «Namespace.Type.Method»
/// (только методы Тропы и .NET), версия Тропы и Windows. Одинаковые ошибки на сервере склеиваются в счётчик.
/// </summary>
public static partial class CrashTelemetry
{
    public static CrashSummary Summarize(Exception ex, string app)
    {
        var root = ex is AggregateException { InnerExceptions.Count: 1 } a ? a.InnerExceptions[0] : ex;
        var frames = new StackTrace(root, fNeedFileInfo: false).GetFrames()
            .Select(f => f.GetMethod())
            .Where(m => m?.DeclaringType is not null)
            .Select(m => $"{m!.DeclaringType!.FullName}.{m.Name}")
            .Select(Clean)
            .Where(f => f.StartsWith("Tropa.", StringComparison.Ordinal) || f.StartsWith("System.", StringComparison.Ordinal) || f.StartsWith("Microsoft.", StringComparison.Ordinal))
            .Distinct()
            .Take(8)
            .ToArray();
        return new CrashSummary(app, TropaBuild.VersionText, Clean(root.GetType().FullName ?? root.GetType().Name), frames,
            $"Windows {Environment.OSVersion.Version} {RuntimeInformation.FrameworkDescription}");
    }

    /// <summary>Имена генерированных компилятором классов (async, лямбды) — к виду, который проходит проверку сервера.</summary>
    private static string Clean(string name) => FrameRe().Replace(name, "").Trim('.');

    [GeneratedRegex(@"[^A-Za-z0-9_.`<>+]")]
    private static partial Regex FrameRe();

    public static async Task<bool> SendAsync(string baseUrl, CrashSummary summary, CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Tropa/{TropaBuild.Version}");
            using var r = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/v1/crash", new
            {
                app = summary.App,
                version = summary.Version,
                type = summary.Type,
                frames = summary.Frames,
                os = summary.Os,
            }, ct);
            return r.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return false; }
    }
}
