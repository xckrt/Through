using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>
/// Движок без winws: пока «запущен» пресет, FakeServer.SniFilter спрашивает AllowConnection,
/// пропускать ли очередное соединение. passes(presetId, номер соединения с запуска) решает,
/// лечит ли пресет, в том числе нестабильно.
/// </summary>
internal sealed class FakeEngineHost(Func<string, int, bool> passes) : IEngineHost
{
    private readonly object _gate = new();
    private string? _active;
    private int _connections;

    public List<string> Started { get; } = [];
    public int Stopped { get; private set; }
    public HashSet<string> FailToStart { get; } = [];
    public EngineLaunch? LastLaunch { get; private set; }
    public bool Running { get { lock (_gate) return _active is not null; } }

    public bool AllowConnection()
    {
        lock (_gate)
            return _active is { } id && passes(id, ++_connections);
    }

    public Task<IEngineSession> StartAsync(EngineLaunch launch, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_active is not null) throw new InvalidOperationException("движок уже запущен");
            Started.Add(launch.Preset.Id);
            if (FailToStart.Contains(launch.Preset.Id)) throw new InvalidOperationException("winws не стартовал");
            _active = launch.Preset.Id;
            _connections = 0;
            LastLaunch = launch;
        }
        return Task.FromResult<IEngineSession>(new Session(this));
    }

    private sealed class Session(FakeEngineHost host) : IEngineSession
    {
        public string Log => "";

        public ValueTask DisposeAsync()
        {
            lock (host._gate)
            {
                host._active = null;
                host.Stopped++;
            }
            return ValueTask.CompletedTask;
        }
    }
}
