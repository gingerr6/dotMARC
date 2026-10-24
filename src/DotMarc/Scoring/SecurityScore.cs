using DotMarc.Data;

namespace DotMarc.Scoring;

/// <summary>One part of a domain's score: the points it earned out of what it's worth, and what would earn the rest.</summary>
public sealed record ScoreFactor(string Name, int Points, int Max, string? Improve)
{
    public bool Full => Points >= Max;
}

public sealed record DomainScore(int Score, string Grade, IReadOnlyList<ScoreFactor> Factors)
{
    /// <summary>What would raise the score most, biggest gain first.</summary>
    public IEnumerable<ScoreFactor> Improvements => Factors.Where(factor => !factor.Full && factor.Improve is not null).OrderByDescending(factor => factor.Max - factor.Points);
}

/// <summary>A 0-100 score and A-F grade for how well a domain is protected against spoofing and interception, from the
/// checks dotMARC already runs and the last 30 days of reports. Parts that don't apply (mail delivery security for a
/// parked domain that sends and receives nothing) are left out, and the rest scaled to 100. So are the domain checks
/// that haven't run yet, or have nothing to check (no website, no registration of its own): they only count once
/// there's a result.</summary>
public static class SecurityScore
{
    /// <summary>The domain's reports' pass rates over the window: DMARC (SPF or DKIM passing) and DKIM alone. Null with
    /// no mail reported.</summary>
    public sealed record ReportRates(double? PassRate, double? DkimPassRate);

    public static DomainScore For(Domain domain, ReportRates rates)
    {
        var parked = domain.SpfCheckStatus == SpfCheckStatus.NullSpf;
        var factors = new List<ScoreFactor>
        {
            DmarcRecord(domain),
            DmarcPolicy(domain),
            Spf(domain),
        };
        if (!parked)
        {
            factors.Add(Dkim(domain, rates.DkimPassRate));
            factors.Add(PassRate(rates.PassRate));
            factors.Add(MtaSts(domain));
            factors.Add(Tlsrpt(domain));
        }

        factors.AddRange(new[] { DomainRegistration(domain), DanglingRecords(domain), WebsiteCertificates(domain), Blocklists(domain) }.OfType<ScoreFactor>());

        var max = factors.Sum(factor => factor.Max);
        var score = (int)Math.Round(100.0 * factors.Sum(factor => factor.Points) / max);
        return new DomainScore(score, GradeOf(score), factors);
    }

    public static string GradeOf(int score) => score switch
    {
        >= 90 => "A",
        >= 75 => "B",
        >= 60 => "C",
        >= 40 => "D",
        _ => "F",
    };

    /// <summary>A client's score: the average of its domains'.</summary>
    public static int? Average(IEnumerable<DomainScore> scores)
    {
        var list = scores.ToList();
        return list.Count == 0 ? null : (int)Math.Round(list.Average(score => score.Score));
    }

    private static ScoreFactor DmarcRecord(Domain domain) => domain.DmarcCheckStatus switch
    {
        DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => new("DMARC record", 15, 15, null),
        DmarcCheckStatus.Misconfigured => new("DMARC record", 5, 15, "Fix the DMARC record: it's published but has errors."),
        _ => new("DMARC record", 0, 15, "Publish a DMARC record."),
    };

    private static ScoreFactor DmarcPolicy(Domain domain)
    {
        var partial = domain.DmarcPercent is { } percent && percent < 100;
        var (points, improve) = domain.DmarcCheckStatus is DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord or DmarcCheckStatus.Misconfigured
            ? domain.DmarcPolicy switch
            {
                DmarcPolicyLevel.Reject when !partial => (30, null),
                DmarcPolicyLevel.Reject => (25, "Apply p=reject to all mail (remove pct=)."),
                DmarcPolicyLevel.Quarantine when !partial => (20, "Move the DMARC policy to p=reject, so spoofed mail is refused."),
                DmarcPolicyLevel.Quarantine => (10, "Apply p=quarantine to all mail, then move to p=reject."),
                _ => (0, "Move the DMARC policy from p=none to quarantine, then reject: until then spoofed mail is still delivered."),
            }
            : (0, (string?)null);
        return new("DMARC enforcement", points, 30, improve);
    }

    private static ScoreFactor Spf(Domain domain) => domain.SpfCheckStatus switch
    {
        SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => new("SPF record", 15, 15, null),
        SpfCheckStatus.TooManyLookups or SpfCheckStatus.Misconfigured or SpfCheckStatus.MultipleRecords => new("SPF record", 5, 15, "Fix the SPF record: receivers can't evaluate it as it is."),
        _ => new("SPF record", 0, 15, "Publish an SPF record listing the services that send as the domain."),
    };

    /// <summary>Full marks when its selectors check out; most of them when they aren't set up in dotMARC but reports show
    /// DKIM passing.</summary>
    private static ScoreFactor Dkim(Domain domain, double? dkimPassRate) => domain.DkimCheckStatus switch
    {
        DkimCheckStatus.Ok => new("DKIM signing", 15, 15, null),
        DkimCheckStatus.NotConfigured when dkimPassRate >= 0.9 => new("DKIM signing", 12, 15, "Add the domain's DKIM selectors in dotMARC so its keys are checked too."),
        DkimCheckStatus.NotConfigured => new("DKIM signing", 0, 15, "Turn on DKIM signing for every service that sends as the domain."),
        _ => new("DKIM signing", 0, 15, "Fix the DKIM records: they're missing or wrong in DNS."),
    };

    private static ScoreFactor PassRate(double? passRate) => passRate switch
    {
        null => new("Mail passing DMARC", 0, 10, "No reports yet: they arrive within a day or two of the DMARC record pointing at dotMARC."),
        >= 0.98 => new("Mail passing DMARC", 10, 10, null),
        >= 0.90 => new("Mail passing DMARC", 5, 10, "Fix the senders failing DMARC (see the Sources tab)."),
        _ => new("Mail passing DMARC", 0, 10, "Fix the senders failing DMARC (see the Sources tab)."),
    };

    private static ScoreFactor MtaSts(Domain domain) => (domain.MtaStsEnabled, domain.MtaStsStatus, domain.MtaStsMode) switch
    {
        (true, MtaStsStatus.Active, MtaStsMode.Enforce) => new("MTA-STS", 10, 10, null),
        (true, MtaStsStatus.Active, _) => new("MTA-STS", 5, 10, "Move MTA-STS from testing to enforce."),
        (true, _, _) => new("MTA-STS", 0, 10, "Finish setting up MTA-STS: its DNS records aren't all in place."),
        _ => new("MTA-STS", 0, 10, "Turn on MTA-STS, so mail to the domain is only delivered encrypted."),
    };

    private static ScoreFactor? DomainRegistration(Domain domain) => domain.DomainExpiryStatus switch
    {
        DomainExpiryStatus.Ok => new("Domain registration", 5, 5, null),
        DomainExpiryStatus.ExpiringSoon => new("Domain registration", 2, 5, "Make sure the domain's registration renews: it expires within 30 days."),
        DomainExpiryStatus.Expired => new("Domain registration", 0, 5, "Renew the domain's registration: it has expired."),
        _ => null,
    };

    private static ScoreFactor? Blocklists(Domain domain) => domain.BlocklistStatus switch
    {
        BlocklistStatus.Clear => new("Not on blocklists", 5, 5, null),
        BlocklistStatus.Listed => new("Not on blocklists", 0, 5, "Get the domain's sending server off the spam blocklists it's on, and find out why it was listed."),
        _ => null,
    };

    private static ScoreFactor? DanglingRecords(Domain domain) => domain.DanglingCnameStatus switch
    {
        DanglingCnameStatus.Ok => new("No takeover risks", 10, 10, null),
        DanglingCnameStatus.OldRecords => new("No takeover risks", 9, 10, "Remove the old subdomain records that point at retired provider names."),
        DanglingCnameStatus.Dangling => new("No takeover risks", 0, 10, "Remove the subdomain records that point at names that no longer exist: someone could take them over."),
        _ => null,
    };

    private static ScoreFactor? WebsiteCertificates(Domain domain) => domain.CertificateStatus switch
    {
        CertificateCheckStatus.Ok => new("Website certificates", 5, 5, null),
        CertificateCheckStatus.ExpiringSoon => new("Website certificates", 2, 5, "Renew the website certificate: it expires within 14 days."),
        CertificateCheckStatus.Invalid => new("Website certificates", 2, 5, "Fix the website certificate: it doesn't cover every address, such as www."),
        CertificateCheckStatus.Expired => new("Website certificates", 0, 5, "Renew the website certificate: it has expired."),
        _ => null,
    };

    private static ScoreFactor Tlsrpt(Domain domain) => domain.TlsrptCheckStatus == TlsrptCheckStatus.Ok
        ? new("TLS reporting", 5, 5, null)
        : new("TLS reporting", 0, 5, "Publish a TLS reporting record.");
}
