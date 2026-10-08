using System.Text.RegularExpressions;

namespace Tropa.Core.Engine;

/// <summary>
/// Какой релиз zapret ставить и чем его проверить (engine.json).
/// SHA-256 закреплён здесь, а не берётся с того же сервера, что и архив:
/// иначе подменённый архив пришёл бы вместе с подменённым хешем.
/// </summary>
public sealed partial record EngineManifest
{
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";

    /// <summary>Зеркала по порядку; {version} подставляется.</summary>
    public string[] Urls { get; init; } = [];

    /// <summary>Потолок на размер архива: защита от бесконечной или раздутой отдачи.</summary>
    public long MaxBytes { get; init; } = 64L * 1024 * 1024;

    public IEnumerable<string> ResolvedUrls => Urls.Select(u => u.Replace("{version}", Version));

    public static EngineManifest Load(string path) => TropaJson.Load<EngineManifest>(path);

    /// <summary>Отказ до любой сети, если манифест не заполнен: качать без хеша нельзя.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || !SafeVersion().IsMatch(Version))
            throw new InvalidDataException("engine.json: не указана версия zapret (например, v71.4)");
        if (!Sha256Hex().IsMatch(Sha256))
            throw new InvalidDataException(
                "engine.json: не указан SHA-256 архива. Возьмите его со страницы релиза bol-van/zapret на GitHub.");
        if (Urls.Length == 0)
            throw new InvalidDataException("engine.json: нет ни одного адреса для скачивания");
        if (ResolvedUrls.Any(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("engine.json: скачивать можно только по https");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeVersion();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}

/// <summary>Распакованный и проверенный релиз.</summary>
/// <param name="Root">Папка релиза, где лежит files/ (её получает {engineDir} в пресетах).</param>
public sealed record EngineInstall(string Root, string WinwsPath, string Version);
