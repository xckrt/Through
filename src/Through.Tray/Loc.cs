using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Markup;

namespace Through.Tray;

public enum LanguageChoice { System, Russian, English }

/// <summary>
/// Язык окна. Ключ — русская строка из кода (через Loc.T и Loc.F, либо local:Tr в XAML), перевод —
/// en.json в ресурсах. Сообщения службы приходят по-русски (служба не знает, какой язык у окна) и переводятся
/// при показе по шаблонам из Core (Loc.Msg): шаблон «… {0} …» превращается в регулярное выражение.
/// Нет перевода — показываем русский: лучше по-русски, чем пусто.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _en = [];
    private static List<(Regex Pattern, string Format)> _patterns = [];

    public static bool English { get; private set; }

    /// <summary>Выбрать язык до создания окна (смена — с перезапуском окна).</summary>
    public static void Use(LanguageChoice choice)
    {
        English = choice switch
        {
            LanguageChoice.English => true,
            LanguageChoice.Russian => false,
            _ => !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
                 && !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("uk", StringComparison.OrdinalIgnoreCase)
                 && !CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("be", StringComparison.OrdinalIgnoreCase),
        };
        if (!English) return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Through.Tray.en.json");
        if (stream is null) return;
        _en = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
        // шаблоны с подстановками — для сообщений службы; длинные раньше коротких, чтобы «… {0} — медленно» не съел общий
        _patterns = _en.Keys.Where(k => k.Contains("{0")).OrderByDescending(k => k.Length)
            .Select(k => (new Regex("^" + Regex.Replace(Regex.Escape(k), @"\\\{(\d+)[^}]*}", m => $"(?<g{m.Groups[1].Value}>.+?)") + "$",
                RegexOptions.Singleline | RegexOptions.CultureInvariant), _en[k]))
            .ToList();
    }

    public static string T(string ru) => English && _en.TryGetValue(ru, out var en) ? en : ru;

    public static string F(string ru, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(ru), args);

    /// <summary>Перевод готовой строки от службы: точное совпадение, иначе шаблон с подстановками (их значения переводятся тоже).</summary>
    public static string Msg(string? ru)
    {
        if (ru is null || !English) return ru ?? "";
        if (_en.TryGetValue(ru, out var exact)) return exact;
        foreach (var (pattern, format) in _patterns)
        {
            var m = pattern.Match(ru);
            if (!m.Success) continue;
            var values = m.Groups.Cast<Group>().Where(g => g.Name.StartsWith('g'))
                .OrderBy(g => int.Parse(g.Name[1..])).Select(g => (object)Msg(g.Value)).ToArray();
            try { return string.Format(CultureInfo.CurrentCulture, format, values); }
            catch (FormatException) { return ru; }
        }
        return ru;
    }

    /// <summary>Русские формы числа (1, 2–4, 5+) или английские (1 / остальное).</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        if (!English) return Wording.RuPlural(n, one, few, many);
        var key = $"{one}|{few}|{many}";
        if (_en.TryGetValue(key, out var en) && en.Split('|') is [var o, var other]) return n == 1 ? o : other;
        return n == 1 ? T(one) : T(many);
    }
}

/// <summary>{local:Tr 'Обзор'} в XAML: перевод на момент загрузки окна.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(text);
}
