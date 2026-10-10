using System.Text.RegularExpressions;
using Tropa.Core.Control;

namespace Tropa.SelfTest;

/// <summary>Сводки об ошибках: только тип и имена методов, никакого текста ошибки.</summary>
internal static class CrashTests
{
    public static IEnumerable<(string, Func<Task>)> All() =>
    [
        ("Сводка об ошибке: без сообщения и путей, кадры проходят проверку сервера", async () =>
        {
            Exception caught;
            try { await Throw(); throw new Exception("не дошли"); }
            catch (IOException ex) { caught = ex; }
            var s = CrashTelemetry.Summarize(caught, "tray");
            var all = string.Join(" ", s.Frames) + s.Type + s.Os;
            Assert(!all.Contains("ivan") && !all.Contains("secret.example") && !all.Contains(@"C:\"), all);
            Assert(s.Type == "System.IO.IOException" && s.Frames.Length > 0, $"{s.Type} {s.Frames.Length}");
            var frame = new Regex(@"^[A-Za-z_][A-Za-z0-9_.`<>+]{0,200}$"); // та же проверка, что в worker.js
            Assert(s.Frames.All(frame.IsMatch) && frame.IsMatch(s.Type), string.Join(" | ", s.Frames));
            Assert(s.Frames.Any(f => f.StartsWith("Tropa.SelfTest.CrashTests")), string.Join(" | ", s.Frames));
        }),
    ];

    private static async Task Throw()
    {
        await Task.Yield();
        throw new IOException(@"C:\Users\ivan\AppData\secret.example не открылся");
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }
}
