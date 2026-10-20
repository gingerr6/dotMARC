namespace DotMarc.IpEnrichment;

public interface IReverseDnsLookup
{
    /// <summary>The host name an IP's reverse DNS (PTR) record gives, only when that name resolves back to the same IP,
    /// or null when there's no such name. Throws when the resolver can't answer, which isn't the same as no name.</summary>
    Task<string?> LookupAsync(string ip, CancellationToken cancellationToken);
}
