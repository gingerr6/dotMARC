namespace DotMarc.Data;

/// <summary>A decision about one sender of one domain's mail (see SenderIdentity): a recognised service, or a single IP.
/// A sender with no row hasn't been reviewed. Once a domain has any approved sender, the UnapprovedSender alert watches
/// it for mail passing DMARC from a sender nobody approved.</summary>
public sealed class SenderApproval
{
    public int Id { get; set; }
    public int DomainId { get; set; }

    /// <summary>A KnownSenders key, or "ip:" and the IP.</summary>
    public required string SenderKey { get; set; }

    public SenderApprovalStatus Status { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset DecidedUtc { get; set; }
}
