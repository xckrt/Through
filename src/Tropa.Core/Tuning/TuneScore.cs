using Tropa.Core.Probing;

namespace Tropa.Core.Tuning;

/// <summary>
/// Насколько хорошо работает сервис: сначала число целей Ok, потом Throttled,
/// потом суммарно, как далеко дошли упавшие (провал на передаче лучше провала на TLS).
/// </summary>
public readonly record struct TuneScore(int Ok, int Throttled, int Progress, int Total) : IComparable<TuneScore>
{
    public bool IsPerfect => Total > 0 && Ok == Total;

    public int CompareTo(TuneScore other) =>
        (Ok, Throttled, Progress).CompareTo((other.Ok, other.Throttled, other.Progress));

    public static TuneScore Of(IReadOnlyCollection<ProbeResult> results) => new(
        results.Count(r => r.Verdict == Verdict.Ok),
        results.Count(r => r.Verdict == Verdict.Throttled),
        results.Sum(Rank),
        results.Count);

    /// <summary>Ранг одного результата: из двух прогонов берём тот, у кого он меньше.</summary>
    public static int Rank(ProbeResult r) => r.Verdict switch
    {
        Verdict.Ok => 7,
        Verdict.Throttled => 6,
        Verdict.Blocked => 1 + (int)(r.FailedAt ?? ProbeStage.Dns), // Dns=1 … Transfer=5
        _ => 0,
    };

    public override string ToString() => Throttled > 0 ? $"{Ok}/{Total} OK, {Throttled} медленно" : $"{Ok}/{Total} OK";
}
