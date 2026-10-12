namespace Through.Core.Engine;

/// <summary>Новая версия Сквозь на GitHub: установщик и его SHA-256 (как его посчитал GitHub).</summary>
public sealed record AppRelease(Version Version, string Tag, string SetupUrl, string SetupName, string Sha256, string PageUrl);

/// <summary>Проверка обновлений самой Сквозь: релизы github.com/xckrt/Through с тегами v1.2.3.</summary>
public static class AppUpdates
{
    public static string SetupName(Version v) => $"Through-Setup-{v.Major}.{v.Minor}.{v.Build}.exe";

    /// <summary>null — обновлений нет. Бросает, если релиз есть, но у установщика нет хеша (такое ставить нельзя).</summary>
    public static async Task<AppRelease?> CheckAsync(HttpClient http, Version current, CancellationToken ct = default)
    {
        var resolver = new ReleaseResolver(http, ThroughBuild.Repo);
        if (await resolver.LatestTagAsync(ct) is not { } tag || ReleaseResolver.Parse(tag) is not { } latest) return null;
        var normalized = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
        if (normalized <= new Version(current.Major, current.Minor, Math.Max(current.Build, 0))) return null;

        var name = SetupName(normalized);
        var sha = await resolver.AssetDigestAsync(tag, name, ct)
                  ?? throw new InvalidDataException($"в релизе {tag} нет установщика {name} с хешем");
        return new AppRelease(normalized, tag, resolver.DownloadUrl(tag, name), name, sha,
            $"https://github.com/{ThroughBuild.Repo}/releases/tag/{tag}");
    }
}
