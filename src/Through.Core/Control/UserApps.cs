using System.Text.Json;
using System.Text.RegularExpressions;
using Through.Core.Probing;
using Through.Core.Tuning;

namespace Through.Core.Control;

/// <summary>
/// Приложение, которое человек добавил сам (игра, сайт). Приходит по каналу от окна без прав администратора,
/// поэтому здесь только данные: домены, хосты для проверки, порты. Никаких аргументов winws и путей:
/// аргументы для игровых портов служба собирает сама из шаблона ниже.
/// </summary>
public sealed record AppDefinition
{
    public required string Key { get; init; }
    public required string Title { get; init; }

    /// <summary>Домены для обхода (с поддоменами).</summary>
    public string[] Domains { get; init; } = [];

    /// <summary>Хосты, по которым подборщик проверяет, работает ли приложение: «host» или «host:port».
    /// Пусто — первые домены на 443.</summary>
    public string[] CheckHosts { get; init; } = [];

    /// <summary>Игровые порты: «7777» или «27000-27100». Обход на них идёт для любого адреса.</summary>
    public string[] UdpPorts { get; init; } = [];
    public string[] TcpPorts { get; init; } = [];

    /// <summary>Обходить, даже если проверка говорит «работает» (игра тормозит, а не отваливается).</summary>
    public bool Always { get; init; }
}

public static partial class UserApps
{
    public const int MaxApps = 50;

    public static AppDefinition Normalize(AppDefinition a)
    {
        static string[] Clean(IEnumerable<string> xs) =>
            xs.Select(x => x.Trim().TrimEnd('.').ToLowerInvariant()).Where(x => x.Length > 0).Distinct().ToArray();

        var n = a with
        {
            Key = a.Key.Trim().ToLowerInvariant(),
            Title = a.Title.Trim(),
            Domains = Clean(a.Domains.Select(StripScheme)),
            CheckHosts = Clean(a.CheckHosts.Select(h => StripScheme(h, keepPort: true))),
            UdpPorts = Clean(a.UdpPorts),
            TcpPorts = Clean(a.TcpPorts),
        };
        if (!KeyRe().IsMatch(n.Key)) throw new InvalidDataException("ключ приложения: латиница, цифры и дефис, до 32 символов");
        if (n.Title.Length is 0 or > 40 || n.Title.Any(char.IsControl)) throw new InvalidDataException("название: от 1 до 40 символов");
        if (n.Domains.Length > 300 || n.CheckHosts.Length > 5) throw new InvalidDataException("слишком много доменов");
        foreach (var d in n.Domains.Concat(n.CheckHosts.Select(h => SplitPort(h).Host)))
            if (!DomainRe().IsMatch(d)) throw new InvalidDataException($"«{d}» не похоже на домен (пример: bhvrdbd.com)");
        foreach (var h in n.CheckHosts)
            if (SplitPort(h).Port is < 1 or > 65535) throw new InvalidDataException($"«{h}»: неверный порт");
        foreach (var p in n.UdpPorts.Concat(n.TcpPorts))
            if (!IsPort(p)) throw new InvalidDataException($"«{p}» не порт и не диапазон (пример: 7777 или 27000-27100)");
        if (n.UdpPorts.Length + n.TcpPorts.Length > 20) throw new InvalidDataException("слишком много портов");
        if (n.Domains.Length == 0 && n.UdpPorts.Length == 0 && n.TcpPorts.Length == 0)
            throw new InvalidDataException("укажите хотя бы один домен или игровой порт");
        return n;
    }

    /// <summary>Каталог целей с приложениями пользователя: свои проверки и домены для обхода.</summary>
    public static TargetCatalog Merge(TargetCatalog builtIn, IReadOnlyList<AppDefinition> apps)
    {
        var services = new Dictionary<string, List<ProbeTarget>>(builtIn.Services);
        var domains = new Dictionary<string, List<string>>(builtIn.Domains);
        foreach (var a in apps)
        {
            var check = a.CheckHosts.Length > 0 ? a.CheckHosts : a.Domains.Take(2).ToArray();
            if (check.Length > 0)
                services[a.Key] = check.Select(h =>
                {
                    var (host, port) = SplitPort(h);
                    return new ProbeTarget { Name = h, Host = host, Port = port };
                }).ToList();
            if (a.Domains.Length > 0) domains[a.Key] = [.. a.Domains];
        }
        return builtIn with { Services = services, Domains = domains };
    }

    /// <summary>Игровые порты превращаются в профили winws по образцу game filter Flowseal.</summary>
    public static PresetCatalog Merge(PresetCatalog builtIn, IReadOnlyList<AppDefinition> apps)
    {
        var companions = new List<CompanionProfile>(builtIn.Companions);
        foreach (var a in apps)
        {
            if (a.UdpPorts.Length > 0)
            {
                var p = string.Join(',', a.UdpPorts);
                companions.Add(new CompanionProfile
                {
                    Id = $"{a.Key}-udp", Service = a.Key, Always = true,
                    Args = [$"--wf-udp={p}", $"--filter-udp={p}", "--dpi-desync=fake", "--dpi-desync-repeats=12",
                            "--dpi-desync-any-protocol=1", "--dpi-desync-fake-unknown-udp={engineDir}/files/extra/ACTIVE_GAME_UDP.bin",
                            "--dpi-desync-cutoff=n2"],
                });
            }
            if (a.TcpPorts.Length > 0)
            {
                var p = string.Join(',', a.TcpPorts);
                companions.Add(new CompanionProfile
                {
                    Id = $"{a.Key}-tcp", Service = a.Key, Always = true,
                    Args = [$"--wf-tcp={p}", $"--filter-tcp={p}", "--dpi-desync=multisplit", "--dpi-desync-any-protocol=1",
                            "--dpi-desync-cutoff=n3", "--dpi-desync-split-seqovl=568", "--dpi-desync-split-pos=1",
                            "--dpi-desync-split-seqovl-pattern={engineDir}/files/extra/tls_clienthello_4pda_to.bin"],
                });
            }
        }
        return builtIn with { Companions = companions };
    }

    public static List<AppDefinition> Load(string path) =>
        File.Exists(path) ? ThroughJson.Load<List<AppDefinition>>(path) : [];

    public static void Save(string path, IReadOnlyList<AppDefinition> apps)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(apps, ThroughJson.Options));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>«https://www.site.com/path» → «www.site.com»; порт оставляется только для хостов проверки.</summary>
    private static string StripScheme(string s) => StripScheme(s, keepPort: false);

    private static string StripScheme(string s, bool keepPort)
    {
        s = s.Trim();
        var i = s.IndexOf("://", StringComparison.Ordinal);
        if (i >= 0) s = s[(i + 3)..];
        var cut = s.IndexOfAny(keepPort ? ['/', '?'] : ['/', ':', '?']);
        if (cut >= 0) s = s[..cut];
        return s.StartsWith("*.") ? s[2..] : s;
    }

    private static (string Host, int Port) SplitPort(string h)
    {
        var c = h.LastIndexOf(':');
        if (c < 0) return (h, 443);
        return (h[..c], int.TryParse(h[(c + 1)..], out var p) ? p : -1);
    }

    private static bool IsPort(string p)
    {
        var parts = p.Split('-');
        if (parts.Length is < 1 or > 2) return false;
        var nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] is < 1 or > 65535) return false;
        return parts.Length == 1 || nums[0] <= nums[1];
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex KeyRe();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{0,62}$")]
    private static partial Regex DomainRe();
}
