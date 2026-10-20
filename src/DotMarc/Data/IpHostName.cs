namespace DotMarc.Data;

/// <summary>An IP's forward-confirmed reverse DNS name (see ReverseDnsLookup), cached per IP. HostName is null when
/// the IP has no such name. Kept apart from IpInfo: owner details are cached per allocation block (IpRange), so many
/// IPs never get an IpInfo row, but a host name belongs to one IP.</summary>
public sealed class IpHostName
{
    public required string Ip { get; set; }
    public string? HostName { get; set; }
    public DateTimeOffset LookedUpUtc { get; set; }
}
