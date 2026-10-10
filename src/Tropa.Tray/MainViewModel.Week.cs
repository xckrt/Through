using Tropa.Core.Control;

namespace Tropa.Tray;

/// <summary>«Неделя с Тропой»: строка на обзоре и одно уведомление со сводкой, когда неделя закончилась.</summary>
public sealed partial class MainViewModel
{
    public string WeekText => _status?.Week?.Current is { } w ? "Эта неделя: " + Summary(w) : "";
    public bool HasWeek => _status?.Week?.Current is { ProtectedHours: > 0.05 } or { TelegramConnections: > 0 } or { DnsQueries: > 0 };

    public static string Summary(WeekReport w)
    {
        var parts = new List<string>();
        if (w.ProtectedHours >= 0.05) parts.Add($"обход {Hours(w.ProtectedHours)}");
        if (w.AutoFixes > 0) parts.Add($"сама починила {w.AutoFixes} {Wording.Plural(w.AutoFixes, "раз", "раза", "раз")}");
        if (w.TelegramBytes > 0) parts.Add($"Telegram {Bytes(w.TelegramBytes)}");
        if (w.DnsQueries > 0) parts.Add($"DNS {w.DnsQueries:N0} {Wording.Plural((int)Math.Min(w.DnsQueries, int.MaxValue), "запрос", "запроса", "запросов")}");
        if (w.YoutubeTimes is > 1.2 and var t) parts.Add($"YouTube ×{t:0.#}");
        return parts.Count == 0 ? "пока ничего" : string.Join(" · ", parts);
    }

    private static string Hours(double h) => h < 1 ? $"{(int)(h * 60)} мин" : $"{h:0.#} ч";

    /// <summary>Неделя закончилась — одно уведомление со сводкой (если человек их не выключил).</summary>
    private void MaybeNotifyWeek(StatusReport s)
    {
        if (s.Week?.Previous is not { } prev) return;
        var key = prev.Start.ToString("O");
        if (_ui.LastWeekNotified == key) return;
        _ui.LastWeekNotified = key;
        _ui.Save();
        Notice?.Invoke("Неделя с Тропой", Summary(prev));
    }

    private void RaiseWeek(StatusReport s)
    {
        Raise(nameof(WeekText));
        Raise(nameof(HasWeek));
        MaybeNotifyWeek(s);
    }
}
