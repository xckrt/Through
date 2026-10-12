using System.Diagnostics;
using Through.Core.Engine;
using Through.Core.Probing;
using Through.Core.Tuning;
using Through.Engine;

/// <summary>through-probe --tune: скачать zapret, подобрать стратегию, сохранить профиль.</summary>
internal static class TuneCommand
{
    public static async Task<int> RunAsync(TargetCatalog catalog, string presetsPath, string[]? only, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            return Fail("подбор работает только в Windows: winws — Windows-программа");
        if (!Environment.IsPrivilegedProcess)
            return Fail("нужны права администратора: winws ставит драйвер WinDivert. Запустите терминал от имени администратора.");
        // чужой winws (zapret, запущенный вручную) испортит диагноз «без обхода» и помешает подбору
        if (Process.GetProcessesByName("winws").Length > 0)
            return Fail("уже запущен winws (похоже, zapret). Остановите его и повторите подбор.");

        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Through");
        var manifest = EngineManifest.Load(Path.Combine(AppContext.BaseDirectory, "engine.json"));
        var presets = PresetCatalog.Load(presetsPath);

        EngineInstall install;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Through/0.1");
            install = await new EngineFetcher(http, Path.Combine(dataDir, "engine"))
                .EnsureAsync(manifest, new SyncProgress<string>(m => Console.WriteLine($"  {m}")), ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
        Console.WriteLine($"winws: {install.WinwsPath}\n");

        var host = new WinwsEngineHost(new WinwsOptions { ExecutablePath = install.WinwsPath, EngineDir = install.Root });
        var report = await new Tuner(host).TuneAsync(catalog, presets, only, ct, new SyncProgress<TuneAttempt>(a =>
            Console.WriteLine(a.Error is { } err
                ? $"  {a.Service,-12} {a.Preset.Id,-28} не запустился: {err}"
                : $"  {a.Service,-12} {a.Preset.Id,-28} {a.Score}")));

        if (report.NetworkDown)
            return Fail("контрольные сайты недоступны: похоже, нет сети. Подбирать нечего.");

        Console.WriteLine();
        foreach (var s in report.Services)
        {
            var line = s.Outcome switch
            {
                TuneOutcome.AlreadyOk => "работает без обхода",
                TuneOutcome.Fixed => $"починен: {s.Best!.Preset.Id}",
                TuneOutcome.Improved => $"стало лучше ({s.Best!.Score}): {s.Best.Preset.Id}",
                TuneOutcome.NotTreatable => "блок по адресу, настройка DPI-обработки не поможет",
                TuneOutcome.NoCandidates => "нет подходящих стратегий",
                _ => $"ни одна из {s.Attempts.Count} стратегий не помогла",
            };
            Console.WriteLine($"{s.Service,-12} {line}");
        }

        var profilePath = Path.Combine(dataDir, "profile.json");
        TuningProfile.Load(profilePath).Apply(report, DateTimeOffset.Now).Save(profilePath);
        Console.WriteLine($"\nпрофиль: {profilePath}");
        return report.Services.All(s => s.Outcome is TuneOutcome.AlreadyOk or TuneOutcome.Fixed) ? 0 : 1;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    /// <summary>Progress&lt;T&gt; шлёт отчёты в пул потоков, и строки консоли перемешиваются; этот вызывает сразу.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
