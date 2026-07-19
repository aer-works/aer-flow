using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Aer.Ui.Core;

/// <summary>
/// Finds a LAN-reachable IPv4 address for this machine (M21 Phase 3: the Enable Remote Access
/// view needs one to put in the pairing QR — the daemon binds <c>IPAddress.Any</c> under
/// <c>--remote</c>, which has no single reachable address of its own).
/// </summary>
public static class LanAddress
{
    /// <summary>
    /// Dev machines routinely carry virtual/VPN adapters (Hyper-V's WSL vEthernet switch, VMware,
    /// VPN TAP/TUN drivers) that report <see cref="OperationalStatus.Up"/> and an RFC 1918 address
    /// just like a real Wi-Fi/Ethernet adapter — .NET's <see cref="NetworkInterfaceType"/> doesn't
    /// distinguish them (a Hyper-V vEthernet switch reports as plain <c>Ethernet</c>), so the only
    /// cross-platform signal is the adapter's own description/name. Found live: this machine's WSL
    /// Hyper-V switch (172.29.176.1) was picked over its real Wi-Fi adapter (192.168.1.72) — the
    /// former is unreachable from a phone on the same physical network, which read on the phone as
    /// a QR code that scans fine but then hangs and fails to connect.
    /// </summary>
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vethernet", "hyper-v", "wsl", "vmware", "virtualbox", "docker",
        "tap-", "tap adapter", "tun", "vpn", "tailscale", "ppp", "loopback",
    ];

    /// <summary>
    /// Picks the most likely LAN-reachable IPv4 address: among physical, non-virtual adapters,
    /// prefers Wi-Fi/Ethernet types and private-range (RFC 1918) addresses over any others (e.g.
    /// carrier-grade NAT or link-local) a multi-adapter machine might report. Falls back to any
    /// non-loopback address if no adapter passes the physical-adapter filter, rather than reporting
    /// no address at all. Returns null if nothing usable is found (e.g. no network connectivity).
    /// </summary>
    public static string? TryGetPrimary()
    {
        var physicalCandidates = new List<string>();
        var allCandidates = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            var isPhysicalType = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211;
            var looksVirtual = VirtualAdapterMarkers.Any(marker =>
                nic.Description.Contains(marker, StringComparison.OrdinalIgnoreCase) ||
                nic.Name.Contains(marker, StringComparison.OrdinalIgnoreCase));

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(addr.Address)) continue;

                var ip = addr.Address.ToString();
                allCandidates.Add(ip);
                if (isPhysicalType && !looksVirtual)
                {
                    physicalCandidates.Add(ip);
                }
            }
        }

        return physicalCandidates.FirstOrDefault(IsPrivateRange) ?? physicalCandidates.FirstOrDefault()
            ?? allCandidates.FirstOrDefault(IsPrivateRange) ?? allCandidates.FirstOrDefault();
    }

    private static bool IsPrivateRange(string ipv4)
    {
        var parts = ipv4.Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second))
        {
            return false;
        }

        return first == 10
            || (first == 172 && second is >= 16 and <= 31)
            || (first == 192 && second == 168);
    }
}
