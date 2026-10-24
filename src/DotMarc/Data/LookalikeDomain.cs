namespace DotMarc.Data;

/// <summary>A registered domain that looks like one dotMARC monitors (see DotMarc.Lookalikes.LookalikeGenerator), as of
/// the last check. Dismissed ones (known, the client's own, harmless) don't count towards the domain's status.</summary>
public sealed class LookalikeDomain
{
    public int Id { get; set; }
    public int DomainId { get; set; }
    public required string Name { get; set; }

    /// <summary>How it differs from the domain, such as "Letter swapped" or "Other ending".</summary>
    public required string Kind { get; set; }

    /// <summary>Whether it has MX records, so it can receive mail (and replies to mail sent from it).</summary>
    public bool HasMx { get; set; }

    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool Dismissed { get; set; }
}
