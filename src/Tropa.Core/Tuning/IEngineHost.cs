namespace Tropa.Core.Tuning;

/// <summary>Что запустить: пресет, сужённый до хостов и портов одного сервиса.</summary>
public sealed record EngineLaunch(StrategyPreset Preset, IReadOnlyList<string> Hosts, IReadOnlyList<int> Ports)
{
    /// <summary>Аргументы для winws: hostlist-файл с Hosts пишет сам движок.</summary>
    public IReadOnlyList<string> RenderArgs(string hostlistPath, string engineDir) =>
        Preset.Args
            .Select(a => a.Replace("{hostlist}", hostlistPath)
                          .Replace("{ports}", string.Join(',', Ports))
                          .Replace("{engineDir}", engineDir))
            .ToList();
}

/// <summary>
/// Запуск и остановка движка DPI-обработки. Настоящая реализация (winws.exe) живёт
/// в Windows-проекте, в Core только контракт: Tuner тестируется с фейковым движком.
/// </summary>
public interface IEngineHost
{
    /// <summary>Возвращается, когда движок готов обрабатывать трафик. Бросает, если не стартовал.</summary>
    Task<IEngineSession> StartAsync(EngineLaunch launch, CancellationToken ct = default);
}

/// <summary>Запущенный движок. DisposeAsync останавливает его.</summary>
public interface IEngineSession : IAsyncDisposable
{
    /// <summary>Что движок успел написать в stderr: для отчёта, если пресет не взлетел.</summary>
    string Log { get; }
}
