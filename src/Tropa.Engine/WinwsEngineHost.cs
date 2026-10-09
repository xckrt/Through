using System.Diagnostics;
using System.Text;
using Tropa.Core.Tuning;

namespace Tropa.Engine;

public sealed record WinwsOptions
{
    public required string ExecutablePath { get; init; }

    /// <summary>Папка релиза zapret: подставляется в {engineDir} пресетов.</summary>
    public required string EngineDir { get; init; }

    /// <summary>Аргументы перед аргументами пресета (тесты: режим фейкового winws).</summary>
    public string[] PrefixArgs { get; init; } = [];

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Строка вывода, после которой winws перехватывает трафик.
    /// Сверить с реальным winws, если в новой версии формулировка поменяется.</summary>
    public string ReadyMarker { get; init; } = "capture is started";
}

/// <summary>
/// Запускает winws.exe с аргументами пресета и ждёт, пока он сообщит о готовности.
/// Нужны права администратора: winws ставит драйвер WinDivert.
/// </summary>
public sealed class WinwsEngineHost(WinwsOptions options) : IEngineHost
{
    public async Task<IEngineSession> StartAsync(IReadOnlyList<EngineLaunch> launches, CancellationToken ct = default)
    {
        if (launches.Count == 0) throw new ArgumentException("нечего запускать", nameof(launches));
        var hostlists = new List<string>();
        foreach (var launch in launches)
        {
            var path = Path.Combine(Path.GetTempPath(), $"tropa-hosts-{Guid.NewGuid():N}.txt");
            hostlists.Add(path);
            await File.WriteAllLinesAsync(path, launch.Hosts, ct);
        }

        var psi = new ProcessStartInfo(options.ExecutablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.ExecutablePath) ?? "",
        };
        foreach (var a in options.PrefixArgs.Concat(EngineLaunch.Compose(launches, hostlists, options.EngineDir)))
            psi.ArgumentList.Add(a);

        var log = new BoundedLog();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            log.Add(e.Data);
            if (e.Data.Contains(options.ReadyMarker, StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
        }
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        // Exited приходит раньше, чем дочитан вывод: дожидаемся конца потоков, чтобы ошибка winws попала в сообщение
        process.Exited += (_, _) => Task.Run(() =>
        {
            try
            {
                process.WaitForExit();
                ready.TrySetException(new InvalidOperationException($"winws завершился с кодом {process.ExitCode}: {log}"));
            }
            catch (InvalidOperationException) { ready.TrySetException(new InvalidOperationException($"winws завершился: {log}")); }
        });

        var session = new WinwsSession(process, hostlists, log);
        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await ready.Task.WaitAsync(options.ReadyTimeout, ct);
            return session;
        }
        catch (TimeoutException)
        {
            await session.DisposeAsync();
            throw new InvalidOperationException(
                $"winws не сообщил о готовности за {options.ReadyTimeout.TotalSeconds:F0} с: {log}");
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}

/// <summary>Запущенный winws. DisposeAsync убивает процесс и убирает временный hostlist.</summary>
public sealed class WinwsSession : IEngineSession
{
    private readonly Process _process;
    private readonly BoundedLog _log;

    internal WinwsSession(Process process, IReadOnlyList<string> hostlistPaths, BoundedLog log)
    {
        _process = process;
        HostlistPaths = hostlistPaths;
        _log = log;
    }

    public IReadOnlyList<string> HostlistPaths { get; }
    public string Log => _log.ToString();

    public bool HasExited
    {
        get
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; } // процесс так и не стартовал
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!HasExited)
            {
                _process.Kill(entireProcessTree: true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(cts.Token);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // уже завершился или не дождались: процесс всё равно убит, держать подбор не будем
        }
        finally
        {
            _process.Dispose();
            foreach (var path in HostlistPaths)
                try { File.Delete(path); } catch (IOException) { }
        }
    }
}

/// <summary>Хвост вывода winws не больше 32 КБ: для сообщения об ошибке хватает, память не течёт.</summary>
internal sealed class BoundedLog
{
    private const int Limit = 32 * 1024;
    private readonly StringBuilder _sb = new();

    public void Add(string line)
    {
        lock (_sb)
        {
            _sb.AppendLine(line);
            if (_sb.Length > Limit) _sb.Remove(0, _sb.Length - Limit);
        }
    }

    public override string ToString()
    {
        lock (_sb) return _sb.Length == 0 ? "(вывода нет)" : _sb.ToString().TrimEnd();
    }
}
