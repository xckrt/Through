using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tropa.Core;

namespace Tropa.Tray;

/// <summary>
/// Отчёты об ошибках. Ничего не уходит молча: падение сохраняется на диск, а отправка — это черновик
/// issue на GitHub, который человек видит целиком и отправляет сам. Токенов в коде нет, слать спам нечем.
/// </summary>
public static class CrashReports
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tropa", "crashes");

    /// <summary>Сохранить падение; вызывается из обработчиков необработанных исключений.</summary>
    public static void Save(Exception ex, string where)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = Path.Combine(Dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, $"где: {where}\n{ex}");
        }
        catch (IOException)
        {
            // если не получилось записать отчёт о падении, падать ещё раз незачем
        }
    }

    /// <summary>Падения, о которых ещё не спросили человека.</summary>
    public static IReadOnlyList<string> Pending() =>
        Directory.Exists(Dir) ? Directory.GetFiles(Dir, "crash-*.txt").OrderBy(f => f).ToList() : [];

    /// <summary>Отмечаем, что о падениях спросили: отправил человек отчёт или нет, второй раз не пристаём.</summary>
    public static void MarkSeen()
    {
        foreach (var f in Pending())
            try { File.Move(f, Path.ChangeExtension(f, ".seen")); }
            catch (IOException) { }
    }

    /// <summary>Текст отчёта: версии, система, что видело окно и служба, и падение, если оно было.</summary>
    public static string Build(string summary, IEnumerable<string> windowLog, string? status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("### Что случилось");
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine("### Окружение");
        sb.AppendLine($"- Тропа {TropaBuild.VersionText} (сборка {TropaBuild.Id})");
        sb.AppendLine($"- {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
        if (status is not null) sb.AppendLine($"- служба: {status}");

        foreach (var crash in Pending())
        {
            sb.AppendLine();
            sb.AppendLine($"### Падение окна ({Path.GetFileNameWithoutExtension(crash)})");
            sb.AppendLine("```");
            sb.AppendLine(Tail(File.ReadAllText(crash), 4000));
            sb.AppendLine("```");
        }

        var lines = windowLog.Take(40).ToList();
        if (lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Журнал окна (новые сверху)");
            sb.AppendLine("```");
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine("```");
        }

        var serviceLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Tropa", "service.log");
        try
        {
            if (File.Exists(serviceLog))
            {
                sb.AppendLine();
                sb.AppendLine("### Журнал службы (последние строки)");
                sb.AppendLine("```");
                foreach (var l in File.ReadLines(serviceLog).TakeLast(60)) sb.AppendLine(l);
                sb.AppendLine("```");
            }
        }
        catch (IOException) { }
        return sb.ToString();
    }

    /// <summary>
    /// Открыть черновик issue в браузере. Адрес не может быть бесконечным, поэтому в нём сокращённый отчёт,
    /// а полный кладётся в буфер обмена. Возвращает true, если отчёт пришлось сократить.
    /// </summary>
    public static bool OpenIssue(string title, string report)
    {
        const int limit = 6000;
        var cut = report.Length > limit;
        var body = cut
            ? report[..limit] + "\n\n…отчёт сокращён: полный текст Тропа скопировала в буфер обмена, вставьте его сюда."
            : report;
        var url = $"https://github.com/{TropaBuild.Repo}/issues/new?labels=bug" +
                  $"&title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        try { System.Windows.Clipboard.SetText(report); }
        catch (Exception) { /* буфер занят другим приложением: черновик всё равно откроется */ }
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        return cut;
    }

    private static string Tail(string s, int max) => s.Length <= max ? s : "…" + s[^max..];
}
