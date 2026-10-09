using System.Text.RegularExpressions;

namespace Tropa.Core.Engine;

/// <summary>
/// Ищет последний релиз zapret на github.com (не через api.github.com: у части провайдеров он недоступен).
/// Хеши берутся из двух мест релиза: SHA-256 архива со страницы ассетов (его считает GitHub)
/// и SHA-256 winws.exe из авторского sha256sum.txt. Это слабее закреплённого в engine.json хеша
/// (доверие к GitHub по TLS), поэтому первая установка идёт с закреплённой версии, а обновления — отсюда.
/// </summary>
public sealed partial class ReleaseResolver(HttpClient http, string repo = "bol-van/zapret")
{
    private const string Base = "https://github.com/";

    /// <summary>Тег последнего релиза: github.com/.../releases/latest перенаправляет на /releases/tag/&lt;тег&gt;.</summary>
    public async Task<string?> LatestTagAsync(CancellationToken ct = default)
    {
        using var response = await http.GetAsync($"{Base}{repo}/releases/latest", HttpCompletionOption.ResponseHeadersRead, ct);
        var final = response.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
        var m = TagInUrl().Match(final);
        return m.Success && SafeTag().IsMatch(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    /// <summary>Манифест для тега: те же зеркала и extras, что в закреплённом, но версия и хеши из релиза.</summary>
    public async Task<EngineManifest> ManifestForAsync(string tag, EngineManifest pinned, CancellationToken ct = default)
    {
        if (!SafeTag().IsMatch(tag)) throw new InvalidDataException($"странный тег релиза: {tag}");

        var zipName = $"zapret-{tag}.zip";
        var zipHash = await AssetDigestAsync(tag, zipName, ct)
                      ?? throw new InvalidDataException($"на странице релиза {tag} нет SHA-256 для {zipName}");

        var sums = await http.GetStringAsync($"{Base}{repo}/releases/download/{tag}/sha256sum.txt", ct);
        var winws = sums.Split('\n')
            .Select(l => l.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(p => p.Length == 2 && p[1].EndsWith("binaries/windows-x86_64/winws.exe", StringComparison.Ordinal));
        if (winws is null || !Hex64().IsMatch(winws[0]))
            throw new InvalidDataException($"в sha256sum.txt релиза {tag} нет winws.exe");

        var manifest = pinned with { Version = tag, Sha256 = zipHash, WinwsSha256 = winws[0] };
        manifest.Validate();
        return manifest;
    }

    /// <summary>SHA-256 файла релиза, как его показывает GitHub на странице ассетов. null — файла или хеша нет.</summary>
    public async Task<string?> AssetDigestAsync(string tag, string assetName, CancellationToken ct = default)
    {
        if (!SafeTag().IsMatch(tag)) throw new InvalidDataException($"странный тег релиза: {tag}");
        var assets = await http.GetStringAsync($"{Base}{repo}/releases/expanded_assets/{tag}", ct);
        foreach (var block in assets.Split("<li", StringSplitOptions.None).Skip(1))
        {
            if (!block.Contains($"/releases/download/{tag}/{assetName}\"", StringComparison.Ordinal)) continue;
            var d = Digest().Match(block);
            return d.Success ? d.Groups[1].Value : null;
        }
        return null;
    }

    public string DownloadUrl(string tag, string assetName) => $"{Base}{repo}/releases/download/{tag}/{assetName}";

    /// <summary>v72.14 новее v72.13; нечисловые теги не сравниваются (null).</summary>
    public static int? Compare(string a, string b) =>
        Parse(a) is { } x && Parse(b) is { } y ? x.CompareTo(y) : null;

    public static Version? Parse(string tag)
    {
        var t = tag.TrimStart('v', 'V');
        if (!t.Contains('.')) t += ".0";
        return Version.TryParse(t, out var v) ? v : null;
    }

    [GeneratedRegex(@"/releases/tag/([^/?#]+)$")]
    private static partial Regex TagInUrl();

    [GeneratedRegex("^v?[0-9]+(\\.[0-9]+){0,3}$")]
    private static partial Regex SafeTag();

    [GeneratedRegex("sha256:([0-9a-f]{64})")]
    private static partial Regex Digest();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Hex64();
}
