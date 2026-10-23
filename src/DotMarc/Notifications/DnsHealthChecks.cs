using DotMarc.Data;

namespace DotMarc.Notifications;

public enum DnsCheckHealth
{
    Passing,
    Failing,

    /// <summary>Not checked yet, not applicable or not set up: never alerts.</summary>
    Ignored
}

/// <summary>One of the DNS health checks, as the alerts see it: which status passes or fails, when it last
/// ran, and which setting controls it.</summary>
public sealed record DnsHealthCheck(
    string Item,
    string AlertType,
    string Label,
    Func<Domain, DnsCheckHealth> Health,
    Func<Domain, DateTimeOffset?> CheckedUtc,
    Func<Domain, string> Status,
    Func<Domain, string?> Detail,
    Func<NotificationSettings, DnsHealthAlertMode> Mode);

public static class DnsHealthChecks
{
    public static IReadOnlyList<DnsHealthCheck> All { get; } =
    [
        new(DnsHealthItems.Dmarc, AlertTypes.DmarcRecordBroken, "DMARC record",
            domain => domain.DmarcCheckStatus switch
            {
                DmarcCheckStatus.Ok => DnsCheckHealth.Passing,
                DmarcCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.DmarcCheckedUtc, domain => domain.DmarcCheckStatus.ToString(), domain => domain.DmarcCheckDetail,
            settings => settings.DmarcAlertMode),
        new(DnsHealthItems.DmarcAuthorization, AlertTypes.DmarcAuthorizationBroken, "DMARC authorization record",
            domain => domain.DmarcAuthorizationCheckStatus switch
            {
                DmarcAuthorizationCheckStatus.Ok => DnsCheckHealth.Passing,
                DmarcAuthorizationCheckStatus.Missing => DnsCheckHealth.Failing,
                _ => DnsCheckHealth.Ignored
            },
            domain => domain.DmarcAuthorizationCheckedUtc, domain => domain.DmarcAuthorizationCheckStatus.ToString(), domain => domain.DmarcAuthorizationCheckDetail,
            settings => settings.DmarcAuthorizationAlertMode),
        new(DnsHealthItems.Tlsrpt, AlertTypes.TlsrptRecordBroken, "TLS-RPT record",
            domain => domain.TlsrptCheckStatus switch
            {
                TlsrptCheckStatus.Ok => DnsCheckHealth.Passing,
                TlsrptCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.TlsrptCheckedUtc, domain => domain.TlsrptCheckStatus.ToString(), domain => domain.TlsrptCheckDetail,
            settings => settings.TlsrptAlertMode),
        new(DnsHealthItems.Spf, AlertTypes.SpfRecordBroken, "SPF record",
            domain => domain.SpfCheckStatus switch
            {
                // A null SPF record (-all only) is the healthy state for a domain that sends no mail.
                SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => DnsCheckHealth.Passing,
                SpfCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.SpfCheckedUtc, domain => domain.SpfCheckStatus.ToString(), domain => domain.SpfCheckDetail,
            settings => settings.SpfAlertMode),
        new(DnsHealthItems.Mx, AlertTypes.MxRecordBroken, "MX",
            domain => domain.MxCheckStatus switch
            {
                MxCheckStatus.Ok or MxCheckStatus.NullMx => DnsCheckHealth.Passing,
                MxCheckStatus.NotChecked => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.MxCheckedUtc, domain => domain.MxCheckStatus.ToString(), domain => domain.MxCheckDetail,
            settings => settings.MxAlertMode),
        new(DnsHealthItems.Dkim, AlertTypes.DkimRecordBroken, "DKIM",
            domain => domain.DkimCheckStatus switch
            {
                DkimCheckStatus.Ok => DnsCheckHealth.Passing,
                DkimCheckStatus.NotConfigured => DnsCheckHealth.Ignored,
                _ => DnsCheckHealth.Failing
            },
            domain => domain.DkimCheckedUtc, domain => domain.DkimCheckStatus.ToString(), domain => domain.DkimCheckDetail,
            settings => settings.DkimAlertMode),
        new(DnsHealthItems.MtaSts, AlertTypes.MtaStsFailing, "MTA-STS",
            domain => !domain.MtaStsEnabled
                ? DnsCheckHealth.Ignored
                : domain.MtaStsStatus switch
                {
                    MtaStsStatus.Active => DnsCheckHealth.Passing,
                    MtaStsStatus.Failed => DnsCheckHealth.Failing,
                    // Still being set up: not a failure yet.
                    _ => DnsCheckHealth.Ignored
                },
            domain => domain.MtaStsCheckedUtc, domain => domain.MtaStsStatus.ToString(), domain => domain.MtaStsCheckDetail,
            settings => settings.MtaStsAlertMode),
        new(DnsHealthItems.DanglingCname, AlertTypes.DanglingCname, "Dangling CNAME",
            domain => domain.DanglingCnameStatus switch
            {
                DanglingCnameStatus.Ok => DnsCheckHealth.Passing,
                DanglingCnameStatus.Dangling => DnsCheckHealth.Failing,
                _ => DnsCheckHealth.Ignored
            },
            domain => domain.DanglingCnameCheckedUtc, domain => domain.DanglingCnameStatus.ToString(), domain => domain.DanglingCnameDetail,
            settings => settings.DanglingCnameAlertMode),
        new(DnsHealthItems.Certificate, AlertTypes.CertificateExpiring, "Website certificates",
            domain => domain.CertificateStatus switch
            {
                CertificateCheckStatus.Ok => DnsCheckHealth.Passing,
                CertificateCheckStatus.ExpiringSoon or CertificateCheckStatus.Expired or CertificateCheckStatus.Invalid => DnsCheckHealth.Failing,
                // Not checked yet, or nothing on HTTPS to check.
                _ => DnsCheckHealth.Ignored
            },
            domain => domain.CertificateCheckedUtc, domain => domain.CertificateStatus.ToString(), domain => domain.CertificateDetail,
            settings => settings.CertificateAlertMode),
        new(DnsHealthItems.DomainExpiry, AlertTypes.DomainExpiring, "Domain registration",
            domain => domain.DomainExpiryStatus switch
            {
                DomainExpiryStatus.Ok => DnsCheckHealth.Passing,
                DomainExpiryStatus.ExpiringSoon or DomainExpiryStatus.Expired => DnsCheckHealth.Failing,
                // Not checked yet, or no registration to check (a subdomain).
                _ => DnsCheckHealth.Ignored
            },
            domain => domain.DomainExpiryCheckedUtc, domain => domain.DomainExpiryStatus.ToString(), domain => domain.DomainExpiryDetail,
            settings => settings.DomainExpiryAlertMode),
    ];

    public static DnsHealthCheck For(string item) => All.Single(check => check.Item == item);
}
