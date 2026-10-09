using System.IO.Compression;
using System.Security.Cryptography;

namespace Tropa.Core.Engine;

/// <summary>
/// Скачивает релиз zapret, сверяет SHA-256 с engine.json и распаковывает в installRoot/версия.
/// Бинарей в репозитории нет: всё приходит отсюда. Зеркала пробуются по очереди,
/// установка атомарная: распаковка во временную папку и переименование.
/// </summary>
public sealed class EngineFetcher(HttpClient http, string installRoot)
{
    private const string Marker = ".tropa-sha256";

    public async Task<EngineInstall> EnsureAsync(EngineManifest manifest, IProgress<string>? log = null, CancellationToken ct = default)
    {
        manifest.Validate();
        var dir = Path.Combine(installRoot, manifest.Version);

        var install = Installed(dir, manifest);
        if (install is not null)
            log?.Report($"zapret {manifest.Version} уже установлен");
        else
            install = await DownloadReleaseAsync(manifest, dir, log, ct);

        await EnsureExtrasAsync(install.Root, manifest, log, ct);
        return install;
    }

    /// <summary>Дополнительные файлы (шаблоны ClientHello Flowseal) в files/extra, каждый со своим хешем.</summary>
    private async Task EnsureExtrasAsync(string root, EngineManifest m, IProgress<string>? log, CancellationToken ct)
    {
        var dir = Path.Combine(root, "files", "extra");
        foreach (var extra in m.Extras)
        {
            var path = Path.Combine(dir, extra.Name);
            if (File.Exists(path) && Hash(await File.ReadAllBytesAsync(path, ct)).Equals(extra.Sha256, StringComparison.OrdinalIgnoreCase))
                continue;

            log?.Report($"скачиваю {extra.Name}");
            using var response = await http.GetAsync(extra.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (data.Length > EngineExtra.MaxBytes) throw new InvalidDataException($"{extra.Name}: файл больше {EngineExtra.MaxBytes / 1024} КБ");
            var got = Hash(data);
            if (!got.Equals(extra.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 {extra.Name} не совпал: ожидали {extra.Sha256.ToLowerInvariant()}, получили {got.ToLowerInvariant()}");

            Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data, ct);
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private async Task<EngineInstall> DownloadReleaseAsync(EngineManifest manifest, string dir, IProgress<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(installRoot);
        var errors = new List<string>();
        foreach (var url in manifest.ResolvedUrls)
        {
            var zip = Path.Combine(installRoot, $".download-{Guid.NewGuid():N}.zip");
            try
            {
                log?.Report($"скачиваю {url}");
                await DownloadAsync(url, zip, manifest, ct);
                log?.Report("SHA-256 совпал, распаковываю");
                return Install(zip, dir, manifest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                errors.Add($"{url}: {ex.Message}");
                log?.Report($"не вышло: {ex.Message}");
            }
            finally
            {
                File.Delete(zip);
            }
        }
        throw new InvalidOperationException("не удалось получить zapret ни с одного зеркала:\n" + string.Join("\n", errors));
    }

    private async Task DownloadAsync(string url, string path, EngineManifest m, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > m.MaxBytes)
            throw new InvalidDataException($"архив больше {m.MaxBytes / 1024 / 1024} МБ");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(path))
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                total += n;
                if (total > m.MaxBytes) throw new InvalidDataException($"архив больше {m.MaxBytes / 1024 / 1024} МБ");
                hash.AppendData(buf, 0, n);
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
            }
        }

        var got = Convert.ToHexString(hash.GetHashAndReset());
        if (!got.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA-256 не совпал: ожидали {m.Sha256.ToLowerInvariant()}, получили {got.ToLowerInvariant()}");
    }

    private static EngineInstall Install(string zip, string dir, EngineManifest m)
    {
        // защита от «zip-бомбы»: распакованное не может быть в разы больше потолка
        using (var archive = ZipFile.OpenRead(zip))
        {
            var unpacked = archive.Entries.Sum(e => e.Length);
            if (unpacked > m.MaxBytes * 4)
                throw new InvalidDataException($"распакованный архив слишком большой ({unpacked / 1024 / 1024} МБ)");
        }

        var staging = dir + $".staging-{Guid.NewGuid():N}";
        try
        {
            // ExtractToDirectory сам отказывается писать за пределы папки (../ в именах)
            ZipFile.ExtractToDirectory(zip, staging);
            if (Locate(staging) is not { } found)
                throw new InvalidDataException("в архиве нет winws.exe");
            if (m.WinwsSha256.Length > 0 && Environment.Is64BitOperatingSystem)
            {
                using var exe = File.OpenRead(found.Winws);
                var got = Convert.ToHexString(SHA256.HashData(exe));
                if (!got.Equals(m.WinwsSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 winws.exe не совпал: ожидали {m.WinwsSha256.ToLowerInvariant()}, получили {got.ToLowerInvariant()}");
            }
            File.WriteAllText(Path.Combine(staging, Marker), m.Sha256.ToLowerInvariant());

            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.Move(staging, dir);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        return Locate(dir) is { } loc ? new EngineInstall(loc.Root, loc.Winws, m.Version) : throw new InvalidDataException("в архиве нет winws.exe");
    }

    private static EngineInstall? Installed(string dir, EngineManifest m)
    {
        var marker = Path.Combine(dir, Marker);
        if (!File.Exists(marker) || !File.ReadAllText(marker).Trim().Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return Locate(dir) is { } loc ? new EngineInstall(loc.Root, loc.Winws, m.Version) : null;
    }

    /// <summary>winws.exe нужной разрядности и корень релиза (папка с files/) над ним.</summary>
    private static (string Root, string Winws)? Locate(string dir)
    {
        var candidates = Directory.EnumerateFiles(dir, "winws.exe", SearchOption.AllDirectories).ToList();
        if (candidates.Count == 0) return null;
        var arch = Environment.Is64BitOperatingSystem ? new[] { "x86_64", "win64", "x64" } : ["x86", "win32"];
        var winws = candidates.FirstOrDefault(c => arch.Any(a => c.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    ?? candidates[0];

        var root = Path.GetDirectoryName(winws)!;
        while (!Directory.Exists(Path.Combine(root, "files")) && Path.GetDirectoryName(root) is { } up && up.Length >= dir.Length)
            root = up;
        return (Directory.Exists(Path.Combine(root, "files")) ? root : dir, winws);
    }
}
