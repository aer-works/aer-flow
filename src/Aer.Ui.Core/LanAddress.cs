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
    /// Picks the most likely LAN-reachable IPv4 address: the first non-loopback, operational
    /// adapter's unicast IPv4 address, preferring private-range (RFC 1918) addresses over any
    /// others (e.g. carrier-grade NAT or link-local) a multi-adapter machine might report.
    /// Returns null if nothing usable is found (e.g. no network connectivity at all).
    /// </summary>
    public static string? TryGetPrimary()
    {
        var candidates = new List<string>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(addr.Address)) continue;

                candidates.Add(addr.Address.ToString());
            }
        }

        return candidates.FirstOrDefault(IsPrivateRange) ?? candidates.FirstOrDefault();
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
