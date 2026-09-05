using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PenBridge.Networking;

public sealed record NetworkChoice(string DisplayName, IPAddress Address, bool IsLikelyPhysical);

/// <summary>
/// Dns.GetHostEntry(Dns.GetHostName()) — the previous approach — returns whatever IPv4 the OS
/// happens to list first, which on a real machine can just as easily be a Hyper-V/VMware/WSL/
/// Tailscale virtual adapter as the Wi-Fi the iPad can actually reach. This enumerates every
/// up, non-loopback IPv4 address so the UI can list them and default to a real Wi-Fi/Ethernet one.
/// </summary>
public static class NetworkAdapters
{
    public static IReadOnlyList<NetworkChoice> Enumerate()
    {
        var results = new List<NetworkChoice>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            bool likelyPhysical = nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet
                && !nic.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                && !nic.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                && !nic.Description.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                && !nic.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase);

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                results.Add(new NetworkChoice($"{nic.Name} — {addr.Address}", addr.Address, likelyPhysical));
            }
        }
        return results;
    }

    public static NetworkChoice? PickDefault(IReadOnlyList<NetworkChoice> choices) =>
        choices.FirstOrDefault(c => c.IsLikelyPhysical) ?? choices.FirstOrDefault();
}
