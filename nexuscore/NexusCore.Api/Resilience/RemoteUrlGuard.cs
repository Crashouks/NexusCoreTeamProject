using System.Net;
using System.Net.Sockets;

namespace NexusCore.Api.Resilience;

/// <summary>Thrown when an outbound request is refused because it targets a non-public address (SSRF guard).</summary>
public sealed class BlockedDestinationException(string message) : HttpRequestException(message);

/// <summary>
/// SSRF protection for outbound requests to user-supplied URLs. The check is enforced at
/// connect time (after DNS resolution), so DNS-rebinding and redirects to internal hosts are covered too.
/// </summary>
public static class RemoteUrlGuard
{
    public static bool IsBlocked(Exception? ex)
    {
        for (; ex != null; ex = ex.InnerException)
            if (ex is BlockedDestinationException) return true;
        return false;
    }

    /// <summary>True only for globally routable unicast addresses.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
                return false;
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork) return false;

        var b = address.GetAddressBytes();
        return !(b[0] == 0                                   // 0.0.0.0/8
              || b[0] == 10                                  // 10.0.0.0/8
              || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)  // 100.64.0.0/10 CGNAT (also Tailscale)
              || b[0] == 127                                 // loopback
              || (b[0] == 169 && b[1] == 254)                // link-local, cloud metadata 169.254.169.254
              || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)   // 172.16.0.0/12
              || (b[0] == 192 && b[1] == 168)                // 192.168.0.0/16
              || (b[0] == 192 && b[1] == 0 && b[2] == 0)     // 192.0.0.0/24
              || (b[0] == 198 && (b[1] == 18 || b[1] == 19)) // benchmarking
              || b[0] >= 224);                               // multicast + reserved
    }

    /// <summary>
    /// <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the host itself, refuses to
    /// connect to non-public addresses, and connects to the exact address it validated.
    /// </summary>
    public static async ValueTask<Stream> GuardedConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var allowed = addresses.Where(IsPublicAddress).ToArray();
        if (allowed.Length == 0)
            throw new BlockedDestinationException("Destination address is not allowed");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
