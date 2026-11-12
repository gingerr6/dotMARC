using System.Net;
using System.Net.Sockets;

namespace DotMarc.Senders;

/// <summary>Tests whether an IP is inside a CIDR prefix. Used for the published ranges on <see cref="KnownSender"/>,
/// which only apply when a source has no forward-confirmed hostname. Invalid text does not match and does not throw.</summary>
public static class CidrMatcher
{
    public readonly record struct Prefix(IPAddress Network, int Length);

    public static bool TryParse(string cidr, out Prefix prefix)
    {
        prefix = default;
        if (string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        var slash = cidr.IndexOf('/');
        if (slash <= 0 || slash != cidr.LastIndexOf('/'))
        {
            return false;
        }

        if (!IPAddress.TryParse(cidr[..slash], out var network) || !int.TryParse(cidr[(slash + 1)..], out var length))
        {
            return false;
        }

        network = Normalise(network);
        var max = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (network.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6) || length < 0 || length > max)
        {
            return false;
        }

        prefix = new Prefix(network, length);
        return true;
    }

    /// <summary>True when <paramref name="address"/> is inside <paramref name="cidr"/>. <paramref name="prefixLength"/>
    /// is the prefix length on a match, so a caller can prefer the most specific range.</summary>
    public static bool Contains(string cidr, IPAddress address, out int prefixLength)
    {
        prefixLength = 0;
        if (!TryParse(cidr, out var prefix))
        {
            return false;
        }

        address = Normalise(address);
        if (address.AddressFamily != prefix.Network.AddressFamily)
        {
            return false;
        }

        var left = address.GetAddressBytes();
        var right = prefix.Network.GetAddressBytes();
        var fullBytes = prefix.Length / 8;
        var remainingBits = prefix.Length % 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        if (remainingBits != 0)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((left[fullBytes] & mask) != (right[fullBytes] & mask))
            {
                return false;
            }
        }

        prefixLength = prefix.Length;
        return true;
    }

    /// <summary>An IPv4 address written as an IPv4-mapped IPv6 address is the same host.</summary>
    public static IPAddress Normalise(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
