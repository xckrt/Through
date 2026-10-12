using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Through.Core.Control;

namespace Through.Service;

/// <summary>
/// Подслушать, куда ходит программа: ETW-сессия на N секунд с двумя провайдерами Windows.
/// Microsoft-Windows-DNS-Client — какие имена спрашивал процесс (и какие адреса получил),
/// Microsoft-Windows-Kernel-Network — TCP-соединения и UDP-пакеты процесса с адресом и портом.
/// Нужны права администратора, поэтому это делает служба. Содержимое трафика не читается — только адреса.
/// </summary>
internal static class EtwAppObserver
{
    private static readonly Guid DnsClient = new("1C95126E-7EEA-49A9-A3FE-A378B03DDB4D");
    private static readonly Guid KernelNetwork = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    public static Task<ObservedTraffic> ObserveAsync(string process, TimeSpan duration, CancellationToken ct) =>
        Task.Run(() => Observe(process, duration, ct), ct);

    private static ObservedTraffic Observe(string process, TimeSpan duration, CancellationToken ct)
    {
        var pids = new ConcurrentDictionary<int, byte>();
        void RefreshPids()
        {
            foreach (var p in Process.GetProcessesByName(process))
                using (p) pids[p.Id] = 0;
        }
        RefreshPids();

        var hosts = new ConcurrentBag<string>();
        var ipToHost = new ConcurrentDictionary<string, string>();
        var endpoints = new ConcurrentDictionary<(string, int, string), byte>();

        using var session = new TraceEventSession($"Through-observe-{Guid.NewGuid():N}") { StopOnDispose = true };
        session.EnableProvider(DnsClient);
        session.EnableProvider(KernelNetwork, TraceEventLevel.Informational, 0x10 | 0x20); // KERNEL_NETWORK_KEYWORD_IPV4 | IPV6

        session.Source.Dynamic.All += e =>
        {
            try
            {
                if (e.ProviderGuid == DnsClient) OnDns(e);
                else if (e.ProviderGuid == KernelNetwork) OnNet(e);
            }
            catch (Exception) { /* одно кривое событие не должно ронять наблюдение */ }
        };

        void OnDns(TraceEvent e)
        {
            if (e.PayloadByName("QueryName") is not string name || name.Length == 0) return;
            if (pids.ContainsKey(e.ProcessID)) hosts.Add(name);
            // адреса из ответа запоминаем для всех: соединение может открыть другой поток или процесс-помощник
            if (e.PayloadByName("QueryResults") is string results)
                foreach (var part in results.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ip = part.Trim().Replace("::ffff:", "");
                    if (IPAddress.TryParse(ip, out _)) ipToHost[ip] = name;
                }
        }

        void OnNet(TraceEvent e)
        {
            // TcpIp/Connect, UdpIp/Send (IPv4 и IPv6): PID в полезной нагрузке, у ядра в заголовке бывает 0
            var name = e.EventName ?? "";
            var udp = name.Contains("Udp", StringComparison.OrdinalIgnoreCase);
            var tcp = name.Contains("Connect", StringComparison.OrdinalIgnoreCase) || name.Contains("Send", StringComparison.OrdinalIgnoreCase);
            if (!udp && !tcp) return;
            var pid = Convert.ToInt32(e.PayloadByName("PID") ?? e.ProcessID);
            if (!pids.ContainsKey(pid)) return;
            var addr = e.PayloadByName("daddr")?.ToString();
            var portRaw = e.PayloadByName("dport");
            if (addr is null || portRaw is null) return;
            var port = Convert.ToInt32(portRaw) & 0xFFFF;
            if (IPAddress.TryParse(addr, out var ip) && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any))) return;
            endpoints[(addr, port, udp ? "udp" : "tcp")] = 0;
        }

        var pump = Task.Run(() => session.Source.Process());
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            Thread.Sleep(1000);
            RefreshPids(); // программа могла запуститься уже во время прослушивания
        }
        session.Stop();
        pump.Wait(TimeSpan.FromSeconds(3));

        // порядок байт порта: в манифесте он сетевой, но кто его переворачивает, зависит от версии разбора.
        // Решаем по данным: 443, 80 и 53 есть почти всегда — видны «как есть» или перевёрнутыми
        static int Swap(int p) => (p >> 8 & 0xFF) | (p & 0xFF) << 8;
        var raw = endpoints.Keys.Select(k => k.Item2).ToHashSet();
        // переворачиваем порт только при прямом доказательстве: видны перевёрнутые 443/80/53, а прямых нет.
        // без такого якоря НЕ угадываем — иначе игровой порт вроде 7777 превращается в мусор (баг «что он ищет»)
        var swap = !raw.Overlaps([443, 80, 53]) && raw.Overlaps([Swap(443), Swap(80), Swap(53)]);
        var fixedEndpoints = endpoints.Keys.Select(k => (k.Item1, Port: swap ? Swap(k.Item2) : k.Item2, k.Item3)).Distinct();

        return new ObservedTraffic
        {
            Process = process,
            Hosts = hosts.Distinct().ToArray(),
            Endpoints = fixedEndpoints.Select(k => new ObservedEndpoint(k.Item1, k.Port, k.Item3, ipToHost.GetValueOrDefault(k.Item1))).ToArray(),
            Seconds = (int)duration.TotalSeconds,
            ProcessFound = !pids.IsEmpty,
        };
    }
}
