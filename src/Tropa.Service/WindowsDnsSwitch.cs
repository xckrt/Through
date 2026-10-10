using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Win32;
using Tropa.Core.Doh;

namespace Tropa.Service;

/// <summary>
/// DNS адаптеров Windows: указать 127.0.0.1 и ::1 (там слушает SecureDnsServer) и вернуть прежнее.
/// Прежние настройки — в dns-restore.json: файл переживает падение службы, и при следующем старте или удалении
/// Тропы DNS вернётся, даже если служба умерла посреди работы.
/// </summary>
internal sealed class WindowsDnsSwitch(string dataDir, FileLog log) : IDnsSwitch
{
    private readonly string _path = Path.Combine(dataDir, "dns-restore.json");

    /// <summary>Что было у адаптера: null — серверы от DHCP, иначе статический список.</summary>
    private sealed record Saved(int Index, string Id, string Name, string[]? V4, string[]? V6);

    public async Task<int> PointToLocalAsync(CancellationToken ct)
    {
        var saved = Load();
        var count = 0;
        foreach (var nic in Candidates())
        {
            var p = nic.GetIPProperties();
            var index = p.GetIPv4Properties()?.Index ?? p.GetIPv6Properties()?.Index ?? -1;
            if (index < 0) continue;
            // прежние настройки запоминаем один раз: повторное включение не должно записать в «прежние» наш 127.0.0.1
            if (saved.All(s => s.Id != nic.Id))
                saved.Add(new Saved(index, nic.Id, nic.Name, StaticServers(nic.Id, v6: false), StaticServers(nic.Id, v6: true)));
            Save(saved);
            await NetshAsync($"interface ipv4 set dnsservers name={index} source=static address=127.0.0.1 register=none validate=no", ct);
            if (nic.Supports(NetworkInterfaceComponent.IPv6))
                await NetshAsync($"interface ipv6 set dnsservers name={index} source=static address=::1 register=none validate=no", ct);
            count++;
        }
        await FlushAsync(ct);
        return count;
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var saved = Load();
        if (saved.Count == 0) return;
        var present = NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Id).ToHashSet();
        foreach (var s in saved.Where(s => present.Contains(s.Id)))
        {
            try
            {
                await Apply(s.Index, "ipv4", s.V4, ct);
                await Apply(s.Index, "ipv6", s.V6, ct);
            }
            catch (InvalidOperationException ex)
            {
                log.Write($"DNS адаптера «{s.Name}» не вернулся: {ex.Message}");
            }
        }
        File.Delete(_path);
        await FlushAsync(ct);
    }

    private static async Task Apply(int index, string family, string[]? servers, CancellationToken ct)
    {
        if (servers is not { Length: > 0 })
        {
            await NetshAsync($"interface {family} set dnsservers name={index} source=dhcp", ct);
            return;
        }
        await NetshAsync($"interface {family} set dnsservers name={index} source=static address={servers[0]} register=primary validate=no", ct);
        for (var i = 1; i < servers.Length; i++)
            await NetshAsync($"interface {family} add dnsservers name={index} address={servers[i]} index={i + 1} validate=no", ct);
    }

    /// <summary>Подключённые адаптеры с выходом в сеть: Wi-Fi, Ethernet; без петли, туннелей и виртуальных без шлюза.</summary>
    private static IEnumerable<NetworkInterface> Candidates() =>
        NetworkInterface.GetAllNetworkInterfaces().Where(n =>
            n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any)));

    /// <summary>Статические DNS адаптера из реестра (NameServer). Пусто — DHCP.</summary>
    private static string[]? StaticServers(string id, bool v6)
    {
        var key = v6 ? $@"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters\Interfaces\{id}"
                     : $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{id}";
        using var k = Registry.LocalMachine.OpenSubKey(key);
        var value = k?.GetValue("NameServer") as string;
        var list = value?.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s is not "127.0.0.1" and not "::1").ToArray();
        return list is { Length: > 0 } ? list : null;
    }

    private List<Saved> Load()
    {
        try { return File.Exists(_path) ? JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(_path)) ?? [] : []; }
        catch (JsonException) { return []; }
    }

    private void Save(List<Saved> saved)
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(saved));
        File.Move(tmp, _path, overwrite: true);
    }

    private static async Task NetshAsync(string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo("netsh.exe", args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException($"netsh {args}: {output.Trim()}");
    }

    private static async Task FlushAsync(CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo("ipconfig.exe", "/flushdns") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        await p.WaitForExitAsync(ct);
    }
}
