namespace DotMarc.Data;

/// <summary>The result of the most recent website certificate check for a Domain - see
/// DotMarc.Dns.CertificateChecker. NotChecked is listed first so it is the enum's (and the database column's)
/// default value.</summary>
public enum CertificateCheckStatus
{
    NotChecked,

    /// <summary>None of the hosts checked answers on HTTPS, so there's nothing to check.</summary>
    NoWebsite,
    Ok,

    /// <summary>A certificate expires within <see cref="DotMarc.Dns.CertificateChecker.ExpiringSoonDays"/> days.
    /// Automatic renewal normally happens well before that, so it usually means renewal is failing.</summary>
    ExpiringSoon,
    Expired,

    /// <summary>A certificate doesn't match the host's name or isn't trusted.</summary>
    Invalid
}
