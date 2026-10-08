namespace Tropa.Core.Probing;

/// <summary>
/// Прогоняет набор целей параллельно и отсекает ложные тревоги:
/// если упали все контрольные хосты, проблема в сети, а не в блокировке.
/// </summary>
public sealed class ProbeRunner(ServiceProber prober, int parallelism = 4)
{
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(
        IEnumerable<ProbeTarget> targets,
        CancellationToken ct = default)
    {
        var list = targets.ToList();
        var results = new ProbeResult[list.Count];
        using var gate = new SemaphoreSlim(parallelism);

        await Task.WhenAll(list.Select(async (target, i) =>
        {
            await gate.WaitAsync(ct);
            try { results[i] = await prober.ProbeAsync(target, ct); }
            finally { gate.Release(); }
        }));

        var controls = results.Where(r => r.Target.IsControl).ToList();
        var networkDown = controls.Count > 0 && controls.All(r => !r.IsOk);
        if (!networkDown) return results;

        return results
            .Select(r => r.IsOk ? r : r with
            {
                Verdict = Verdict.Inconclusive,
                Detail = $"контрольные хосты недоступны, похоже на проблему сети. {r.Detail}",
            })
            .ToArray();
    }
}
