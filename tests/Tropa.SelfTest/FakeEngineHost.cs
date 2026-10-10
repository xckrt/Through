using Tropa.Core.Tuning;

namespace Tropa.SelfTest;

/// <summary>
/// Движок без winws: пока «запущены» пресеты, FakeServer.SniFilter спрашивает AllowConnection,
/// пропускать ли очередное соединение. passes(presetId, номер соединения с запуска) решает,
/// лечит ли пресет, в том числе нестабильно. Соединение проходит, если его лечит любой из запущенных.
/// </summary>
internal sealed class FakeEngineHost(Func<string, int, bool> passes) : IEngineHost
{
    private readonly object _gate = new();
    private string[]? _active;
    private int _connections;
    private Session? _session;

    public List<string> Started { get; } = [];
    public int Stopped { get; private set; }
    public HashSet<string> FailToStart { get; } = [];
    public IReadOnlyList<EngineLaunch> LastLaunches { get; private set; } = [];
    public EngineLaunch? LastLaunch => LastLaunches.LastOrDefault();
    public bool Running { get { lock (_gate) return _active is not null; } }
    public IReadOnlyList<string> Active { get { lock (_gate) return _active ?? []; } }

    public bool AllowConnection()
    {
        lock (_gate)
        {
            if (_active is null) return false;
            var n = ++_connections;
            return _active.Any(id => passes(id, n));
        }
    }

    /// <summary>Как настоящий winws с профилями через --new: к хосту применяется пресет того профиля,
    /// в чей hostlist он входит. null, если движок не запущен или хост ни в одном профиле.</summary>
    public string? PresetFor(string host)
    {
        lock (_gate)
            return _active is null ? null : LastLaunches.FirstOrDefault(l => l.EffectiveHosts.Contains(host))?.Preset.Id;
    }

    /// <summary>Имитация падения winws посреди работы.</summary>
    public void Crash()
    {
        lock (_gate)
        {
            _active = null;
            if (_session is not null) _session.Dead = true;
        }
    }

    public Task<IEngineSession> StartAsync(IReadOnlyList<EngineLaunch> launches, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_active is not null) throw new InvalidOperationException("движок уже запущен");
            var ids = launches.Select(l => l.Preset.Id).ToArray();
            Started.Add(string.Join("+", ids));
            if (ids.Any(FailToStart.Contains)) throw new InvalidOperationException("winws не стартовал");
            _active = ids;
            _connections = 0;
            LastLaunches = launches;
            _session = new Session(this);
            return Task.FromResult<IEngineSession>(_session);
        }
    }

    private sealed class Session(FakeEngineHost host) : IEngineSession
    {
        public bool Dead;
        private bool _disposed;

        public string Log => "";
        public bool HasExited => Dead || _disposed;

        public ValueTask DisposeAsync()
        {
            lock (host._gate)
            {
                if (_disposed) return ValueTask.CompletedTask;
                _disposed = true;
                if (host._session == this)
                {
                    host._active = null;
                    host._session = null;
                }
                host.Stopped++;
            }
            return ValueTask.CompletedTask;
        }
    }
}
