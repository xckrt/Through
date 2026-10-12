using System.Text.Json;
using System.Text.RegularExpressions;

namespace Through.SelfTest;

/// <summary>Английский интерфейс: у каждой строки окна есть перевод, подстановки {0} в переводе те же.</summary>
internal static partial class LocTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Перевод: все строки окна (Loc.T, Loc.F, {local:Tr}) есть в en.json, подстановки совпадают", () =>
        {
            var tray = FindTray();
            if (tray is null) return Task.CompletedTask; // тесты запущены не из репозитория: проверять нечего
            var en = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(tray, "en.json")))!;
            var missing = new List<string>();
            foreach (var file in Directory.EnumerateFiles(tray, "*.cs"))
                foreach (Match m in CallRe().Matches(File.ReadAllText(file)))
                    Check(Unescape(m.Groups[1].Value), Path.GetFileName(file));
            foreach (Match m in XamlRe().Matches(File.ReadAllText(Path.Combine(tray, "MainWindow.xaml"))))
                Check(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value), "MainWindow.xaml");

            void Check(string key, string where)
            {
                if (!en.TryGetValue(key, out var value)) { missing.Add($"{where}: {key}"); return; }
                var a = PlaceholderRe().Matches(key).Select(x => x.Value).Order();
                var b = PlaceholderRe().Matches(value).Select(x => x.Value).Order();
                if (!a.SequenceEqual(b)) missing.Add($"{where}: подстановки не совпали: {key} → {value}");
            }
            if (missing.Count > 0) throw new Exception("нет перевода (добавьте в src/Through.Tray/en.json):\n  " + string.Join("\n  ", missing.Distinct().Take(20)));
            return Task.CompletedTask;
        }),
    ];

    private static string? FindTray()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "src", "Through.Tray");
            if (File.Exists(Path.Combine(p, "en.json"))) return p;
        }
        return null;
    }

    private static string Unescape(string s) => Regex.Unescape(s.Replace("\\\"", "\""));

    [GeneratedRegex(@"Loc\.[TF]\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex CallRe();

    [GeneratedRegex(@"\{local:Tr '([^']*)'\}")]
    private static partial Regex XamlRe();

    [GeneratedRegex(@"\{\d+[^}]*\}")]
    private static partial Regex PlaceholderRe();
}
