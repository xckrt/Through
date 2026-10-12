using Through.Core.Probing;

/// <summary>--speed: сколько сейчас качает видео YouTube (при том обходе, что сейчас включён, или без него).</summary>
internal static class SpeedCommand
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var r = await YouTubeSpeed.MeasureAsync(ct);
        Console.WriteLine(r.Error is { } e
            ? $"YouTube: {e}"
            : $"YouTube: {r.Mbps:0.#} Мбит/с ({r.Bytes / 1024 / 1024.0:0.#} МБ за {r.Milliseconds} мс с {r.Host})");
        return r.Error is null ? 0 : 1;
    }
}
