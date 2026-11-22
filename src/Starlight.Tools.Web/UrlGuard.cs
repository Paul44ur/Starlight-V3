using System.Net;
using System.Net.Sockets;

namespace Starlight.Tools.Web;

/// <summary>
/// Web tools reach the public web only: http and https, no addresses on this PC or the home network
/// (a page must not steer Starlight to the router or a local service). The user's own servers have their own tools.
/// The check runs when a connection opens, so redirects and DNS tricks are covered too.
/// </summary>
public static class UrlGuard
{
    public static Uri Parse(string text)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            throw new ToolException($"\"{text}\" is not a web address (http or https).");
        }
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !IsPublic(ip) || uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException($"{uri.Host} is on this PC or the local network. The web tools only reach the public web.");
        }
        return uri;
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast))
        {
            return false;
        }
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || b[0] == 0 || b[0] == 127
                || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                || b[0] == 192 && b[1] == 168
                || b[0] == 169 && b[1] == 254
                || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                || b[0] >= 224);
        }
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.IsIPv6UniqueLocal);
    }

    /// <summary>
    /// For the headless browser, which connects by itself: true only for http or https to a host whose every
    /// address is public. Other schemes that load nothing from the network (data, blob, about) are allowed too.
    /// </summary>
    public static async Task<bool> IsPublicAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme is "data" or "blob" or "about")
        {
            return true;
        }
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) || uri.IsLoopback
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip))
        {
            return IsPublic(ip);
        }
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, ct);
            return addresses.Length > 0 && addresses.All(IsPublic);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>A connect step for <see cref="SocketsHttpHandler"/> that refuses non-public addresses.</summary>
    public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var target = addresses.FirstOrDefault(IsPublic)
            ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} is not on the public web.");
        if (addresses.Any(a => !IsPublic(a)))
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} points to a local address.");
        }
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
