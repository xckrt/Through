using System.Text.RegularExpressions;

namespace Through.Core.Engine;

/// <summary>
/// Какой релиз zapret ставить и чем его проверить (engine.json).
/// SHA-256 закреплён здесь, а не берётся с того же сервера, что и архив:
/// иначе подменённый архив пришёл бы вместе с подменённым хешем.
/// </summary>
public sealed partial record EngineManifest
{
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";

    /// <summary>Хеш самого winws.exe (64-битного) из авторского sha256sum.txt: вторая проверка после распаковки.
    /// Пусто — не проверять.</summary>
    public string WinwsSha256 { get; init; } = "";

    /// <summary>Дополнительные файлы поверх релиза (шаблоны ClientHello, которых нет у bol-van), в files/extra.</summary>
    public EngineExtra[] Extras { get; init; } = [];

    /// <summary>Зеркала по порядку; {version} подставляется.</summary>
    public string[] Urls { get; init; } = [];

    /// <summary>Потолок на размер архива: защита от бесконечной или раздутой отдачи.</summary>
    public long MaxBytes { get; init; } = 64L * 1024 * 1024;

    public IEnumerable<string> ResolvedUrls => Urls.Select(u => u.Replace("{version}", Version));

    public static EngineManifest Load(string path) => ThroughJson.Load<EngineManifest>(path);

    /// <summary>Отказ до любой сети, если манифест не заполнен: качать без хеша нельзя.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Version) || !SafeVersion().IsMatch(Version))
            throw new InvalidDataException("engine.json: не указана версия zapret (например, v71.4)");
        if (!Sha256Hex().IsMatch(Sha256))
            throw new InvalidDataException(
                "engine.json: не указан SHA-256 архива. Возьмите его со страницы релиза bol-van/zapret на GitHub.");
        if (WinwsSha256.Length > 0 && !Sha256Hex().IsMatch(WinwsSha256))
            throw new InvalidDataException("engine.json: winwsSha256 должен быть полным SHA-256");
        if (Urls.Length == 0)
            throw new InvalidDataException("engine.json: нет ни одного адреса для скачивания");
        if (ResolvedUrls.Any(u => !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("engine.json: скачивать можно только по https");
        foreach (var e in Extras)
        {
            if (!SafeVersion().IsMatch(e.Name) || e.Name.StartsWith('.'))
                throw new InvalidDataException($"engine.json: недопустимое имя файла {e.Name}");
            if (!Sha256Hex().IsMatch(e.Sha256))
                throw new InvalidDataException($"engine.json: у {e.Name} нет полного SHA-256");
            if (!e.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"engine.json: {e.Name} можно качать только по https");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeVersion();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Hex();
}

/// <summary>Небольшой файл поверх релиза: имя в files/extra, адрес и закреплённый хеш.</summary>
public sealed record EngineExtra(string Name, string Url, string Sha256)
{
    public const int MaxBytes = 1024 * 1024;
}

/// <summary>Распакованный и проверенный релиз.</summary>
/// <param name="Root">Папка релиза, где лежит files/ (её получает {engineDir} в пресетах).</param>
public sealed record EngineInstall(string Root, string WinwsPath, string Version);
