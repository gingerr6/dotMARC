using System.Net;
using System.Net.Sockets;

namespace DotMarc.Dns;

/// <summary>Fetches a small file from an https URL that came from someone else's DNS (a BIMI logo or certificate), refusing
/// to connect anywhere that isn't the public internet, so a record can't point dotMARC at its own network or the cloud
/// metadata service. The address is checked at connect time, after DNS resolution, so a name that resolves to a private
/// address is refused too.</summary>
public sealed class PublicHttpFetcher(HttpClient http)
{
    public sealed record Fetched(byte[]? Body, string? ContentType, string? Error);

    public async Task<Fetched> GetAsync(string url, int maxBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new(null, null, "it isn't an https:// URL");
        }

        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(null, null, $"it returned HTTP {(int)response.StatusCode}");
            }
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return new(null, null, $"it's larger than {maxBytes / 1024} KB");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxBytes)
                {
                    return new(null, null, $"it's larger than {maxBytes / 1024} KB");
                }
            }

            return new(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType, null);
        }
        catch (HttpRequestException exception) when (exception.InnerException is NotPublicAddressException)
        {
            return new(null, null, "it points to a private or internal address");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return new(null, null, "it couldn't be downloaded");
        }
    }

    /// <summary>The handler for this fetcher's HttpClient: connects only to public unicast addresses, no redirects.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(IsPublic) ?? throw new NotPublicAddressException();
            if (addresses.Any(candidate => !IsPublic(candidate)))
            {
                throw new NotPublicAddressException();
            }

            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return false;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !(bytes[0] is 0 or 10 or 127 or >= 224
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
                || (bytes[0] == 198 && bytes[1] is 18 or 19));
        }
        // IPv6: only global unicast (2000::/3), less the documentation range.
        var v6 = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (v6[0] & 0xE0) == 0x20 && !(v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8);
    }

    private sealed class NotPublicAddressException() : IOException("The host doesn't resolve to a public address.");
}
