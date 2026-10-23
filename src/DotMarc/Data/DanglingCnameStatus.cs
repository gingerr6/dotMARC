namespace DotMarc.Data;

/// <summary>The result of the most recent dangling CNAME check for a Domain - see DotMarc.Dns.DanglingCnameChecker.
/// NotChecked is listed first so it is the enum's (and the database column's) default value.</summary>
public enum DanglingCnameStatus
{
    NotChecked,
    Ok,

    /// <summary>A common subdomain is a CNAME to a name that no longer exists: whoever registers or claims that
    /// name can serve content, or receive mail, as the domain.</summary>
    Dangling,

    /// <summary>Only CNAMEs to names that no longer exist under a provider's own domain that nobody else can create
    /// names in (Microsoft's retired Skype for Business hosts under lync.com, say): dead records worth removing, but
    /// not a takeover risk.</summary>
    OldRecords
}
