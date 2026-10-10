using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Tropa.Core.Control;

/// <summary>
/// Одно сообщение канала, одна строка JSON.
/// Запрос: id + method (+ data). Ответ: id + data или error. Событие службы: event + data.
/// </summary>
public sealed record Envelope
{
    public int? Id { get; init; }
    public string? Method { get; init; }
    public string? Event { get; init; }
    public JsonElement? Data { get; init; }
    public string? Error { get; init; }
}

public static class ControlProtocol
{
    public const string PipeName = "tropa";

    /// <summary>Длиннее строк не бывает: защита от клиента, который шлёт бесконечную строку.</summary>
    public const int MaxLine = 64 * 1024;

    public static string Encode(Envelope e) => JsonSerializer.Serialize(e, TropaJson.Line);

    public static Envelope Decode(string line) =>
        JsonSerializer.Deserialize<Envelope>(line, TropaJson.Line) ?? throw new InvalidDataException("пустое сообщение");

    public static JsonElement ToData(object value) => JsonSerializer.SerializeToElement(value, value.GetType(), TropaJson.Line);

    public static T FromData<T>(JsonElement data) =>
        data.Deserialize<T>(TropaJson.Line) ?? throw new InvalidDataException("пустые данные");
}

/// <summary>Буферизованное чтение строк из канала с потолком длины. null = соединение закрыто.</summary>
internal sealed class LineReader(Stream stream)
{
    private readonly byte[] _buf = new byte[8192];
    private readonly MemoryStream _line = new();
    private int _pos, _len;

    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        _line.SetLength(0);
        while (true)
        {
            if (_pos == _len)
            {
                _len = await stream.ReadAsync(_buf, ct);
                _pos = 0;
                if (_len == 0) return _line.Length == 0 ? null : Text();
            }
            var nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            var end = nl < 0 ? _len : nl;
            _line.Write(_buf, _pos, end - _pos);
            if (_line.Length > ControlProtocol.MaxLine) throw new InvalidDataException("слишком длинное сообщение");
            _pos = nl < 0 ? _len : nl + 1;
            if (nl >= 0) return Text();
        }
    }

    private string Text() => Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
}

/// <summary>
/// Сервер канала службы. Методы: status, check, tune {services?}, protect {on},
/// apps, apps.add {AppDefinition}, apps.remove {key}, engine.update, settings.autoUpdate {on},
/// telegram {on}, telegram.check, dns {on}, apps.observe {process, seconds}.
/// По каналу принимаются только имена сервисов и действия: аргументы winws и пути
/// приходят из файлов рядом со службой, а не от клиента.
/// </summary>
public sealed class ControlServer(TropaController controller, string pipeName, Func<NamedPipeServerStream>? createPipe = null)
{
    private readonly ConcurrentDictionary<Connection, byte> _connections = new();

    public async Task RunAsync(CancellationToken ct)
    {
        void Broadcast(string evt, object data)
        {
            var line = ControlProtocol.Encode(new Envelope { Event = evt, Data = ControlProtocol.ToData(data) });
            foreach (var c in _connections.Keys) c.Send(line);
        }

        controller.Notify += Broadcast;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var pipe = createPipe?.Invoke() ?? new NamedPipeServerStream(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                try
                {
                    await pipe.WaitForConnectionAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    await pipe.DisposeAsync();
                    break;
                }

                var conn = new Connection(pipe, controller);
                _connections[conn] = 0;
                _ = conn.RunAsync(ct).ContinueWith(_ => _connections.TryRemove(conn, out byte _), TaskScheduler.Default);
            }
        }
        finally
        {
            controller.Notify -= Broadcast;
            foreach (var c in _connections.Keys) c.Close();
        }
    }

    private sealed class Connection(NamedPipeServerStream pipe, TropaController controller)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public async Task RunAsync(CancellationToken ct)
        {
            var reader = new LineReader(pipe);
            try
            {
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0) continue;
                    Envelope request;
                    try { request = ControlProtocol.Decode(line); }
                    catch (JsonException) { Send(ControlProtocol.Encode(new Envelope { Error = "не JSON" })); continue; }
                    // запросы обслуживаются параллельно: status должен отвечать, пока идёт долгий tune
                    _ = Task.Run(() => HandleAsync(request, ct), ct);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
            {
                // клиент ушёл или прислал мусор: соединение закрываем, служба живёт дальше
            }
            finally
            {
                Close();
            }
        }

        private async Task HandleAsync(Envelope request, CancellationToken ct)
        {
            Envelope response;
            try
            {
                object result = request.Method switch
                {
                    "status" => controller.Status(),
                    "check" => await controller.CheckAsync(ct),
                    "tune" => await controller.TuneAsync(Services(request.Data), ct),
                    "protect" => await controller.SetProtectionAsync(On(request.Data), ct),
                    "apps" => controller.Apps(),
                    "engine.update" => await controller.UpdateEngineAsync(ct),
                    "settings.autoUpdate" => await controller.SetAutoUpdateAsync(On(request.Data), ct),
                    "telegram" => await controller.SetTelegramAsync(On(request.Data), ct),
                    "telegram.check" => await controller.CheckTelegramAsync(ct),
                    "dns" => await controller.SetSecureDnsAsync(On(request.Data), ct),
                    "telegram.lan" => await controller.SetTelegramLanAsync(On(request.Data), ct),
                    "tune.voice" => await controller.TuneVoiceAsync(ct),
                    "speed" => await controller.MeasureSpeedAsync(ct),
                    "settings.community" => await controller.SetCommunityAsync(Bool(request.Data, "use"), Bool(request.Data, "share"), ct),
                    // только имя процесса и число секунд; ответ — адреса и имена, которые программа запрашивала
                    "apps.observe" => await controller.ObserveAppAsync(Str(request.Data, "process"), Int(request.Data, "seconds", 20), ct),
                    // только данные (имя, домены, порты): Normalize в контроллере проверяет каждое поле
                    "apps.add" => await controller.AddAppAsync(request.Data is { } d
                        ? ControlProtocol.FromData<AppDefinition>(d)
                        : throw new InvalidOperationException("apps.add: нет описания приложения"), ct),
                    "apps.remove" => await controller.RemoveAppAsync(Key(request.Data), ct),
                    _ => throw new InvalidOperationException($"неизвестный метод: {request.Method}"),
                };
                response = new Envelope { Id = request.Id, Data = ControlProtocol.ToData(result) };
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                response = new Envelope { Id = request.Id, Error = ex.Message };
            }
            Send(ControlProtocol.Encode(response));
        }

        private static string[]? Services(JsonElement? data) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("services", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : null;

        private static string Key(JsonElement? data) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()!
                : throw new InvalidOperationException("apps.remove: нужен параметр key");

        private static string Str(JsonElement? data, string name) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()!
                : throw new InvalidOperationException($"нужен параметр {name}");

        private static bool Bool(JsonElement? data, string name) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static int Int(JsonElement? data, string name, int fallback) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;

        private static bool On(JsonElement? data) =>
            data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("on", out var on)
                ? on.ValueKind == JsonValueKind.True
                : throw new InvalidOperationException("нужен параметр on");

        public void Send(string line) => _ = SendAsync(line);

        private async Task SendAsync(string line)
        {
            try
            {
                await _write.WaitAsync();
                try
                {
                    await pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
                    await pipe.FlushAsync();
                }
                finally { _write.Release(); }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Close();
            }
        }

        public void Close()
        {
            try { pipe.Dispose(); } catch (IOException) { }
        }
    }
}

/// <summary>Клиент канала: окно и тесты. Ответы сопоставляются с запросами по id, события приходят в Event.</summary>
public sealed class ControlClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<Envelope>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private int _nextId;

    /// <summary>Вызывается в потоке чтения: имя события и данные (см. ControlProtocol.FromData).</summary>
    public event Action<string, JsonElement>? Event;

    /// <summary>Служба закрыла соединение (остановлена или перезапущена).</summary>
    public event Action? Disconnected;

    private ControlClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>null, если служба не запущена.</summary>
    public static async Task<ControlClient?> TryConnectAsync(string pipeName, TimeSpan timeout, CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeout, ct);
            return new ControlClient(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await pipe.DisposeAsync();
            return null;
        }
    }

    public async Task<T> CallAsync<T>(string method, object? data = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            var line = ControlProtocol.Encode(new Envelope
            {
                Id = id, Method = method, Data = data is null ? null : ControlProtocol.ToData(data),
            });
            await _write.WaitAsync(ct);
            try
            {
                await _pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), ct);
                await _pipe.FlushAsync(ct);
            }
            finally { _write.Release(); }

            var response = await tcs.Task.WaitAsync(ct);
            if (response.Error is { } error) throw new InvalidOperationException(error);
            return response.Data is { } d ? ControlProtocol.FromData<T>(d) : throw new InvalidDataException("ответ без данных");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync()
    {
        var reader = new LineReader(_pipe);
        try
        {
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                Envelope e;
                try { e = ControlProtocol.Decode(line); }
                catch (JsonException) { continue; }

                if (e.Event is { } name)
                    Event?.Invoke(name, e.Data ?? default);
                else if (e.Id is { } id && _pending.TryGetValue(id, out var tcs))
                    tcs.TrySetResult(e);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException)
        {
        }
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new IOException("служба закрыла соединение"));
        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _pipe.DisposeAsync();
        try { await _reader; } catch { /* чтение уже остановлено */ }
        _cts.Dispose();
    }
}
