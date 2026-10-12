namespace Through.Core.Probing;

/// <summary>
/// Прогоняет набор целей параллельно и отсекает ложные тревоги:
/// если упали все контрольные хосты, проблема в сети, а не в блокировке.
/// </summary>
public sealed class ProbeRunner(ServiceProber prober, int parallelism = 4)
{
    /// <param name="progress">Получает каждый результат, как только он готов, ещё до поправки
    /// на «нет сети»: итог всегда брать из возвращаемого списка.</param>
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(
        IEnumerable<ProbeTarget> targets,
        CancellationToken ct = default,
        IProgress<ProbeResult>? progress = null)
    {
        var list = targets.ToList();
        var results = new ProbeResult[list.Count];
        using var gate = new SemaphoreSlim(parallelism);

        await Task.WhenAll(list.Select(async (target, i) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[i] = await prober.ProbeAsync(target, ct);
                progress?.Report(results[i]);
            }
            finally { gate.Release(); }
        }));

        if (!IsNetworkDown(results)) return results;

        return results
            .Select(r => r.IsOk ? r : r with
            {
                Verdict = Verdict.Inconclusive,
                Detail = $"контрольные хосты недоступны, похоже на проблему сети. {r.Detail}",
            })
            .ToArray();
    }

    /// <summary>Контрольные хосты были и все упали: нет сети, диагнозам верить нельзя.</summary>
    public static bool IsNetworkDown(IEnumerable<ProbeResult> results)
    {
        var controls = results.Where(r => r.Target.IsControl).ToList();
        return controls.Count > 0 && controls.All(r => !r.IsOk);
    }
}
