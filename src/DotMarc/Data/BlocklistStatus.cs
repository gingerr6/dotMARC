namespace DotMarc.Data;

/// <summary>The result of the most recent blocklist check for a Domain - see DotMarc.Dns.BlocklistChecker. NotChecked is
/// listed first so it is the enum's (and the database column's) default value.</summary>
public enum BlocklistStatus
{
    NotChecked,

    /// <summary>The domain has no sending servers of its own to check: it sends through services like Microsoft 365,
    /// whose shared servers are their provider's to look after.</summary>
    NothingToCheck,
    Clear,
    Listed
}
