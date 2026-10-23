namespace DotMarc.Data;

/// <summary>The result of the most recent registration expiry check for a Domain - see
/// DotMarc.Dns.DomainExpiryChecker. NotChecked is listed first so it is the enum's (and the database column's) default
/// value.</summary>
public enum DomainExpiryStatus
{
    NotChecked,

    /// <summary>The registry has no record of the name: a subdomain (contoso.onmicrosoft.com), or a registry without
    /// RDAP. Nothing to check, never alerts.</summary>
    NotFound,
    Ok,

    /// <summary>The registration expires within <see cref="DotMarc.Dns.DomainExpiryChecker.ExpiringSoonDays"/> days.</summary>
    ExpiringSoon,
    Expired
}
