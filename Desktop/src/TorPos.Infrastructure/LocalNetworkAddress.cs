using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TorPos.Infrastructure;

/// <summary>
/// R103: best-effort LAN IPv4 lookup so the QR/URL points somewhere a
/// customer's phone (on the same shop WiFi) can actually reach - never
/// 127.0.0.1, which would only resolve on the till itself. On a
/// multi-adapter machine this picks the first "Up", non-loopback IPv4
/// address, which is right for the common single-NIC till but could pick
/// the wrong adapter on a machine with e.g. a VPN also active - a known,
/// acceptable limitation.
///
/// R115: moved from TorPos.App into Infrastructure because
/// DigitalReceiptService now binds its listener to this exact address
/// instead of every interface. Having one copy matters here: if the server
/// bound one address while the printed QR advertised another, the link
/// would silently point nowhere.
/// </summary>
public static class LocalNetworkAddress
{
    public static string? FindLanIPv4()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    return addr.Address.ToString();
                }
            }
        }
        catch
        {
            // No network info available - caller treats null as "can't offer this now".
        }
        return null;
    }
}
