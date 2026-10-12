using System.Reflection;

namespace Through.Core;

/// <summary>Версия и отпечаток сборки ядра.</summary>
public static class ThroughBuild
{
    /// <summary>Отпечаток сборки: окно сравнивает свой со службой и обновляет службу, если они разные.</summary>
    public static string Id { get; } = typeof(ThroughBuild).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    /// <summary>Версия Сквозь (Directory.Build.props), по ней сравниваются релизы на GitHub.</summary>
    public static Version Version { get; } =
        Version.TryParse(typeof(ThroughBuild).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0],
            out var v) ? v : new Version(0, 0, 0);

    public static string VersionText => $"{Version.Major}.{Version.Minor}.{Version.Build}";

    public const string Repo = "xckrt/Through";
}
