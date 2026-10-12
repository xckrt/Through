using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Through.Core.Control;

namespace Through.Service;

/// <summary>
/// Какая сейчас сеть, так же как её узнаёт Windows: по MAC-адресу шлюза интерфейса, через который идёт интернет.
/// Имя — из списка сетей Windows (то, что видно в «Параметрах»: имя Wi-Fi или «Сеть 2»).
/// Без ARP (модем, PPPoE) — по адаптеру и адресу шлюза.
/// </summary>
internal static class WindowsNetworkIdentity
{
    public static Task<NetworkId?> CurrentAsync(CancellationToken ct) => Task.Run(Current, ct);

    private static NetworkId? Current()
    {
        var nic = Best();
        if (nic is null) return null;
        var gateway = nic.GetIPProperties().GatewayAddresses
            .Select(g => g.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
        if (gateway is null) return null;

        var mac = Arp(gateway);
        if (mac is not null)
        {
            var hex = Convert.ToHexString(mac);
            return new NetworkId("gw-" + hex, NameFor(mac) ?? nic.Name);
        }
        return new NetworkId($"if-{nic.Id.Trim('{', '}')}-{gateway}", nic.Name);
    }

    /// <summary>Интерфейс, через который Windows пошла бы в интернет.</summary>
    private static NetworkInterface? Best()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();
        if (GetBestInterface(BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes()), out var index) == 0)
        {
            var hit = all.FirstOrDefault(n => n.Supports(NetworkInterfaceComponent.IPv4) && n.GetIPProperties().GetIPv4Properties()?.Index == index);
            if (hit is not null) return hit;
        }
        return all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && n.GetIPProperties().GatewayAddresses.Count > 0);
    }

    private static byte[]? Arp(IPAddress ip)
    {
        var mac = new byte[6];
        var len = (uint)mac.Length;
        var dest = BitConverter.ToUInt32(ip.GetAddressBytes());
        return SendARP(dest, 0, mac, ref len) == 0 && len == 6 && mac.Any(b => b != 0) ? mac : null;
    }

    /// <summary>NetworkList\Signatures: Windows помнит для каждой сети MAC шлюза и её имя.</summary>
    private static string? NameFor(byte[] mac)
    {
        try { return NameFromRegistry(mac); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return null; } // не SYSTEM и не админ
    }

    private static string? NameFromRegistry(byte[] mac)
    {
        foreach (var branch in new[] { "Unmanaged", "Managed" })
        {
            using var root = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Signatures\{branch}");
            if (root is null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("DefaultGatewayMac") is byte[] gw && gw.AsSpan().SequenceEqual(mac))
                    return k.GetValue("FirstNetwork") as string ?? k.GetValue("Description") as string;
            }
        }
        return null;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(uint destIp, uint srcIp, byte[] macAddr, ref uint physAddrLen);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
}
