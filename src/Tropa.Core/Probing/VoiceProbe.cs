using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Tropa.Core.Probing;

/// <summary>
/// Голос Discord: UDP-пакет IP Discovery (как у клиента при входе в голосовой канал) на голосовые серверы
/// ближайших регионов. Список регионов — https://latency.discord.media/rtc, по нему клиент выбирает регион;
/// если он сам не открылся, берём встроенный. Ответ хотя бы одного сервера — голос проходит.
/// Порты зависят от площадки: у Google Cloud 50001–50003, у Cloudflare 19302.
/// </summary>
public sealed partial class ServiceProber
{
    internal static readonly string[] DefaultVoiceAddresses =
    [
        "104.29.158.4", "35.217.63.152", "104.29.146.68", "104.29.159.120", "104.29.155.170", "35.217.47.71",
    ];

    internal static readonly int[] DefaultVoicePorts = [19302, 50001, 50002, 50003];

    private async Task<ProbeResult> VoiceAsync(Run run, CancellationToken ct)
    {
        var t = run.Target;
        run.Enter(ProbeStage.Dns);
        var (addresses, fromList) = await VoiceAddressesAsync(t, ct);
        if (addresses.Count == 0) return run.Fail(Failure.DnsFailed, "не нашлось ни одного голосового сервера");

        run.Enter(ProbeStage.Transfer);
        var ports = _o.VoicePorts ?? DefaultVoicePorts;
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var ssrc = (uint)Random.Shared.Next(1, int.MaxValue);
        var packet = new byte[74];
        BinaryPrimitives.WriteUInt16BigEndian(packet, 1);     // запрос
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 70);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), ssrc);

        var sw = Stopwatch.StartNew();
        // два захода: одиночный UDP-пакет в пути теряется и без всякой блокировки
        for (var round = 0; round < 2; round++)
        {
            foreach (var a in addresses)
                foreach (var p in ports)
                {
                    try { await udp.SendAsync(packet, new IPEndPoint(a, p), ct); }
                    catch (SocketException) { }
                }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(_o.VoiceTimeout);
            try
            {
                while (true)
                {
                    var r = await udp.ReceiveAsync(wait.Token);
                    // ответ — тип 2 с нашим SSRC; любой другой ответ сервера тоже значит, что UDP ходит
                    if (r.Buffer.Length >= 8 && addresses.Contains(r.RemoteEndPoint.Address))
                    {
                        run.Address = r.RemoteEndPoint.Address;
                        return run.Done(Verdict.Ok, Failure.None,
                            $"голосовой сервер {r.RemoteEndPoint} ответил за {sw.ElapsedMilliseconds} мс{(fromList ? "" : " (список регионов не открылся, проверены известные адреса)")}");
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (SocketException) { } // ICMP «порт недоступен» от одного из адресов
        }

        return run.Fail(Failure.UdpBlocked,
            $"UDP до голосовых серверов Discord не доходит: {addresses.Count} адресов × порты {string.Join(",", ports)} молчат. " +
            "Похоже, голос режут: в канале будет «Подключение к RTC» без конца");
    }

    /// <summary>Голосовые серверы ближайших регионов: по первому адресу из пяти первых регионов.</summary>
    private async Task<(List<IPAddress> Addresses, bool FromList)> VoiceAddressesAsync(ProbeTarget t, CancellationToken ct)
    {
        if (_o.VoiceAddresses is { } fixedList) return ([.. fixedList.Select(IPAddress.Parse)], true);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
            var json = await http.GetStringAsync($"https://{t.Host}{t.Path}", cts.Token);
            var list = new List<IPAddress>();
            foreach (var region in JsonDocument.Parse(json).RootElement.EnumerateArray().Take(5))
                if (region.TryGetProperty("ips", out var ips) && ips.EnumerateArray().FirstOrDefault().GetString() is { } ip
                    && IPAddress.TryParse(ip, out var a))
                    list.Add(a);
            if (list.Count > 0) return (list, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { }
        return ([.. DefaultVoiceAddresses.Select(IPAddress.Parse)], false);
    }
}
