using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Tropa.Core.Probing;

/// <summary>
/// Проверяет доступность сервиса по этапам (DNS → TCP → TLS → HTTP → передача)
/// и говорит, на каком именно этапе и каким способом соединение сломали.
/// Это основа подборщика стратегий: «YouTube не работает» бесполезно,
/// «RST после ClientHello» сразу говорит, что нужна десинхронизация по SNI.
/// </summary>
public sealed partial class ServiceProber(ProbeOptions? options = null)
{
    private readonly ProbeOptions _o = options ?? ProbeOptions.Default;

    public async Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct = default)
    {
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(_o.TotalTimeout);

        var run = new Run(target, Stopwatch.StartNew());
        try
        {
            return await ProbeCoreAsync(run, total.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return run.Fail(Failure.Error, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<ProbeResult> ProbeCoreAsync(Run run, CancellationToken ct)
    {
        var t = run.Target;
        if (t.Voice) return await VoiceAsync(run, ct);

        // ---- DNS
        run.Enter(ProbeStage.Dns);
        IPAddress[] addresses;
        try
        {
            using var cts = Stage(ct, _o.DnsTimeout);
            var resolver = _o.Resolver ?? SystemResolveAsync;
            addresses = await resolver(t.Host, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return run.Fail(Failure.DnsFailed, "таймаут DNS");
        }
        catch (SocketException ex)
        {
            return run.Fail(Failure.DnsFailed, ex.SocketErrorCode.ToString());
        }

        if (addresses.Length == 0)
            return run.Fail(Failure.DnsFailed, "пустой ответ");

        var bogus = _o.AllowPrivateAddresses ? null : addresses.FirstOrDefault(IsBogus);
        if (bogus is not null)
        {
            run.Address = bogus;
            return run.Fail(Failure.DnsBogus, $"DNS вернул {bogus}: подмена ответа или заглушка");
        }

        // ---- TCP: пробуем несколько адресов, IPv4 вперёд
        run.Enter(ProbeStage.Tcp);
        Socket? socket = null;
        (Failure f, string d) lastTcp = (Failure.TcpTimeout, "");
        foreach (var addr in addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                                      .Take(_o.MaxAddresses))
        {
            run.Address = addr;
            var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var cts = Stage(ct, _o.ConnectTimeout);
                await s.ConnectAsync(new IPEndPoint(addr, t.Port), cts.Token);
                socket = s;
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastTcp = (Failure.TcpTimeout, $"{addr}: нет ответа на SYN");
            }
            catch (SocketException ex)
            {
                lastTcp = (MapTcp(ex.SocketErrorCode), $"{addr}: {ex.SocketErrorCode}");
            }
            if (socket is null) s.Dispose();
        }
        if (socket is null)
            return run.Fail(lastTcp.f, lastTcp.d);

        await using var net = new NetworkStream(socket, ownsSocket: true);

        // ---- TLS
        run.Enter(ProbeStage.Tls);
        SslPolicyErrors certErrors = SslPolicyErrors.None;
        var ssl = new SslStream(net, leaveInnerStreamOpen: false);
        await using var _ = ssl;
        try
        {
            using var cts = Stage(ct, _o.TlsTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = t.Host, // SNI: именно по нему DPI и режет
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                {
                    certErrors = errors;
                    return !_o.ValidateCertificate || errors == SslPolicyErrors.None;
                },
            }, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            run.SniFiltered = await NeutralSniAnswersAsync(run.Address!, t.Port, ct);
            return run.Fail(Failure.TlsTimeout, "ClientHello ушёл, ServerHello не пришёл" + SniVerdict(run.SniFiltered));
        }
        catch (AuthenticationException ex) when (certErrors != SslPolicyErrors.None)
        {
            return run.Fail(Failure.TlsCertInvalid, $"{certErrors}: возможно, заглушка провайдера ({ex.Message})");
        }
        catch (Exception ex) when (IsReset(ex, out var how))
        {
            run.SniFiltered = await NeutralSniAnswersAsync(run.Address!, t.Port, ct);
            return run.Fail(Failure.TlsReset, $"{how} во время рукопожатия" + SniVerdict(run.SniFiltered));
        }

        // ---- HTTP
        run.Enter(ProbeStage.Http);
        var request = $"GET {t.Path} HTTP/1.1\r\nHost: {t.Host}\r\nUser-Agent: {_o.UserAgent}\r\n" +
                      (t.WebSocket
                          ? "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n" +
                            $"Sec-WebSocket-Key: {Convert.ToBase64String(Guid.NewGuid().ToByteArray())}\r\n\r\n"
                          : "Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");
        try
        {
            using var cts = Stage(ct, _o.TlsTimeout);
            await ssl.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
        }
        catch (Exception ex) when (IsReset(ex, out var how))
        {
            return run.Fail(Failure.TransferReset, $"{how} при отправке запроса");
        }

        // скорость меряем от отправки запроса: на быстром канале тело приходит вместе с заголовками
        var transferClock = Stopwatch.StartNew();
        var buf = new byte[16 * 1024];
        var head = new MemoryStream();
        long totalBytes = 0;
        int headerEnd = -1;

        // заголовки
        while (headerEnd < 0)
        {
            var (n, failure) = await ReadAsync(ssl, buf, ct);
            if (failure is { } rf)
                return run.Fail(rf == Failure.TransferStalled ? Failure.HttpNoResponse : rf,
                    rf == Failure.TransferStalled ? "TLS поднялся, но ответа на запрос нет" : "обрыв до заголовков ответа");
            if (n == 0)
                return run.Fail(Failure.HttpNoResponse, "соединение закрыто до заголовков ответа");

            totalBytes += n;
            head.Write(buf, 0, n);
            headerEnd = IndexOfHeaderEnd(head.GetBuffer(), (int)head.Length);
            if (headerEnd < 0 && head.Length > 64 * 1024)
                return run.Fail(Failure.Error, "заголовки длиннее 64 КБ");
        }

        var headerText = Encoding.ASCII.GetString(head.GetBuffer(), 0, headerEnd);
        run.HttpStatus = ParseStatus(headerText);
        if (t.WebSocket && run.HttpStatus != 101)
            return run.Fail(Failure.HttpNoResponse, $"websocket не поднялся: сервер ответил HTTP {run.HttpStatus?.ToString() ?? "?"}");
        if (IsBlockPage(run.HttpStatus, headerText, out var why))
            return run.Fail(Failure.HttpBlockPage, why);

        // ---- передача тела
        run.Enter(ProbeStage.Transfer);
        long body = head.Length - (headerEnd + 4);
        while (body < t.MinBytes)
        {
            var (n, failure) = await ReadAsync(ssl, buf, ct);
            if (failure is { } ff)
            {
                run.BodyBytes = body;
                var inWindow = totalBytes >= _o.FreezeWindowMin && totalBytes <= _o.FreezeWindowMax;
                var detail = ff == Failure.TransferStalled
                    ? inWindow
                        ? $"поток встал на {totalBytes / 1024.0:F1} КБ: почерк «заморозки 16 КБ» (ТСПУ)"
                        : $"поток встал на {totalBytes / 1024.0:F1} КБ"
                    : $"обрыв на {totalBytes / 1024.0:F1} КБ";
                return run.Fail(ff, detail);
            }
            if (n == 0) break; // сервер закончил раньше, чем мы ожидали
            totalBytes += n;
            body += n;
        }
        transferClock.Stop();

        run.BodyBytes = body;
        double? kbps = transferClock.Elapsed.Ticks > 0
            ? totalBytes * 8 / 1000.0 / transferClock.Elapsed.TotalSeconds
            : null;
        run.Kbps = kbps;

        if (t.MinKbps is { } min && kbps is { } got && got < min)
            return run.Done(Verdict.Throttled, Failure.Slow, $"{got:F0} кбит/с при пороге {min:F0}");

        var note = body < t.MinBytes ? $"ответ короче ожидаемого ({body} из {t.MinBytes} Б)" : null;
        return run.Done(Verdict.Ok, Failure.None, note);
    }

    /// <summary>Чтение с детектом простоя: возвращает либо байты, либо причину провала.</summary>
    private async Task<(int n, Failure? failure)> ReadAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        using var idle = Stage(ct, _o.ReadIdleTimeout);
        try
        {
            return (await s.ReadAsync(buf, idle.Token), null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (0, Failure.TransferStalled);
        }
        catch (Exception ex) when (IsReset(ex, out _))
        {
            return (0, Failure.TransferReset);
        }
    }

    /// <summary>
    /// Новое соединение на тот же IP и порт, рукопожатие с нейтральным SNI, любой сертификат.
    /// true: сервер ответил (рукопожатие или TLS-alert), значит режут по имени.
    /// false: таймаут или RST и тут, значит глушат TLS на адресе.
    /// null: проверка выключена или не удалось даже подключиться.
    /// </summary>
    private async Task<bool?> NeutralSniAnswersAsync(IPAddress addr, int port, CancellationToken ct)
    {
        if (_o.NeutralSni is not { } sni) return null;

        using var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var cts = Stage(ct, _o.ConnectTimeout);
            await s.ConnectAsync(new IPEndPoint(addr, port), cts.Token);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }

        await using var ssl = new SslStream(new NetworkStream(s, ownsSocket: false), leaveInnerStreamOpen: false);
        try
        {
            using var cts = Stage(ct, _o.TlsTimeout);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            }, cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return true; // TLS-alert: сервер живой, просто не любит этот SNI
        }
        catch (Exception ex) when (IsReset(ex, out _))
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // непонятный сбой проверки не должен ломать основной диагноз
        }
    }

    private string SniVerdict(bool? sniFiltered) => sniFiltered switch
    {
        true => $"; с SNI {_o.NeutralSni} тот же IP отвечает: фильтр по SNI",
        false => $"; с SNI {_o.NeutralSni} тоже нет ответа: глушат весь TLS на этом IP",
        null when _o.NeutralSni is not null => "; проверить с нейтральным SNI не удалось",
        null => "",
    };

    // ---------- helpers

    private static CancellationTokenSource Stage(CancellationToken parent, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static Task<IPAddress[]> SystemResolveAsync(string host, CancellationToken ct) =>
        Dns.GetHostAddressesAsync(host, ct);

    private static Failure MapTcp(SocketError e) => e switch
    {
        SocketError.ConnectionRefused => Failure.TcpRefused,
        SocketError.ConnectionReset => Failure.TcpReset,
        SocketError.TimedOut => Failure.TcpTimeout,
        SocketError.HostUnreachable or SocketError.NetworkUnreachable => Failure.TcpUnreachable,
        _ => Failure.Error,
    };

    /// <summary>RST (ConnectionReset) или неожиданный EOF: оба способа DPI оборвать сессию.</summary>
    internal static bool IsReset(Exception ex, out string how)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException se && se.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
            {
                how = "RST";
                return true;
            }
        }
        if (ex is IOException)
        {
            how = "обрыв (EOF)";
            return true;
        }
        how = "";
        return false;
    }

    internal static bool IsBogus(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) return true;
        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6UniqueLocal;

        var b = a.GetAddressBytes();
        return b[0] == 0
            || b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] >= 224;
    }

    private static int IndexOfHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        return -1;
    }

    private static int? ParseStatus(string headers)
    {
        var end = headers.IndexOf('\r');
        var parts = (end >= 0 ? headers[..end] : headers).Split(' ', 3);
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : null;
    }

    private static readonly string[] BlockPageMarkers =
        ["warning.rt.ru", "blocked", "zapret-info", "eais.rkn", "block.mts", "blacklist"];

    private static bool IsBlockPage(int? status, string headers, out string why)
    {
        if (status == 451)
        {
            why = "HTTP 451: недоступно по юридическим причинам";
            return true;
        }
        if (status is >= 300 and < 400)
        {
            var location = headers.Split("\r\n")
                .FirstOrDefault(l => l.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));
            var marker = location is null ? null
                : BlockPageMarkers.FirstOrDefault(m => location.Contains(m, StringComparison.OrdinalIgnoreCase));
            if (marker is not null)
            {
                why = $"редирект на страницу блокировки: {location![9..].Trim()}";
                return true;
            }
        }
        why = "";
        return false;
    }

    /// <summary>Состояние одного прогона: тайминги этапов и сбор результата.</summary>
    private sealed class Run(ProbeTarget target, Stopwatch clock)
    {
        private readonly List<StageTiming> _timings = [];
        private ProbeStage? _stage;
        private TimeSpan _stageStart;

        public ProbeTarget Target => target;
        public IPAddress? Address;
        public int? HttpStatus;
        public long BodyBytes;
        public double? Kbps;
        public bool? SniFiltered;

        public void Enter(ProbeStage stage)
        {
            Close();
            _stage = stage;
            _stageStart = clock.Elapsed;
        }

        private void Close()
        {
            if (_stage is { } s) _timings.Add(new StageTiming(s, clock.Elapsed - _stageStart));
        }

        public ProbeResult Fail(Failure failure, string detail)
        {
            var at = _stage;
            var verdict = failure is Failure.Error ? Verdict.Inconclusive : Verdict.Blocked;
            return Build(verdict, failure, detail, at);
        }

        public ProbeResult Done(Verdict verdict, Failure failure, string? detail) =>
            Build(verdict, failure, detail, failure == Failure.None ? null : _stage);

        private ProbeResult Build(Verdict verdict, Failure failure, string? detail, ProbeStage? at)
        {
            Close();
            _stage = null;
            return new ProbeResult
            {
                Target = target,
                Verdict = verdict,
                Failure = failure,
                FailedAt = at,
                Address = Address,
                HttpStatus = HttpStatus,
                BodyBytes = BodyBytes,
                Kbps = Kbps,
                Elapsed = clock.Elapsed,
                Detail = detail,
                SniFiltered = SniFiltered,
                Timings = _timings.ToArray(),
            };
        }
    }
}
