using System.Globalization;
using DotMarc.Data;

namespace DotMarc.Reporting.ClientReports;

/// <summary>The "what to do next" list: fixed rules with fixed wording, never free text, in the order the spec gives.</summary>
public static class ClientReportNextSteps
{
    public const string NothingToDo = "Nothing to do. Every domain is protected.";
    public const string NoDomains = "No domains are being reported on yet.";
    private const double FailingSenderShare = 0.05;

    public static IReadOnlyList<string> For(IReadOnlyList<Domain> domains, IReadOnlyList<ClientReportDomain> reportDomains, CultureInfo? culture = null)
    {
        if (domains.Count == 0)
        {
            return [NoDomains];
        }

        var rows = reportDomains.ToDictionary(row => row.Name, StringComparer.OrdinalIgnoreCase);
        var steps = new List<string>();

        // No policy found is monitoring too, as the portal and the status column treat it, unless there's no record at
        // all, which gets the single step of publishing one below.
        foreach (var domain in domains.Where(domain => domain.DmarcPolicy == DmarcPolicyLevel.None
            || (domain.DmarcPolicy is null && domain.DmarcCheckStatus != DmarcCheckStatus.MissingOwnRecord)))
        {
            steps.Add($"Move {domain.Name} from monitoring to a quarantine policy, so mail that fails DMARC is sent to spam.");
        }

        foreach (var domain in domains.Where(domain => domain.DmarcPolicy == DmarcPolicyLevel.Quarantine && !CheckSteps(domain).Any()))
        {
            steps.Add($"When you're ready, move {domain.Name} to a reject policy.");
        }

        steps.AddRange(domains.SelectMany(CheckSteps));

        foreach (var domain in domains)
        {
            if (!rows.TryGetValue(domain.Name, out var row) || row.Messages == 0)
            {
                continue;
            }

            foreach (var sender in row.TopSenders.Where(sender => sender.Owner is not null && (double)sender.Failing / row.Messages > FailingSenderShare))
            {
                steps.Add(string.Create(culture ?? CultureInfo.InvariantCulture,
                    $"{sender.Owner} sent {sender.Failing:N0} messages as {domain.Name} that failed DMARC. If they send for you, add them to SPF or set up DKIM for them."));
            }
        }

        foreach (var domain in domains.Where(domain => !rows.TryGetValue(domain.Name, out var row) || row.Messages == 0))
        {
            steps.Add($"No DMARC reports arrived for {domain.Name}. Check its DMARC record's reporting address.");
        }

        return steps.Count == 0 ? [NothingToDo] : steps.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> CheckSteps(Domain domain)
    {
        var name = domain.Name;
        switch (domain.DomainExpiryStatus)
        {
            case DomainExpiryStatus.Expired: yield return $"The registration for {name} has expired. Renew it now: its mail and website stop when the registry suspends it."; break;
            case DomainExpiryStatus.ExpiringSoon when domain.DomainExpiresUtc is { } expires: yield return $"The registration for {name} expires on {UkTime.Date(expires)}. Make sure it renews."; break;
        }

        if (domain.BlocklistStatus == BlocklistStatus.Listed)
        {
            yield return $"A server {name} sends mail from is on a spam blocklist, so its mail may go to spam or bounce. Find out why, fix it and ask for delisting.";
        }
        if (domain.LookalikeStatus == LookalikeStatus.Found)
        {
            yield return $"Someone has registered a domain that looks like {name} and set it up for mail. Warn staff and suppliers to check sender addresses, especially on payment requests.";
        }

        var reportingStep = $"Add the record that lets {name}'s DMARC reports reach your reporting mailbox.";
        switch (domain.DmarcCheckStatus)
        {
            case DmarcCheckStatus.MissingOwnRecord: yield return $"Publish a DMARC record for {name}."; break;
            case DmarcCheckStatus.Misconfigured: yield return $"Fix the DMARC record for {name}. Receivers may ignore it as it is."; break;
            case DmarcCheckStatus.MissingAuthorizationRecord: yield return reportingStep; break;
        }

        if (domain.DmarcAuthorizationCheckStatus == DmarcAuthorizationCheckStatus.Missing) yield return reportingStep;

        switch (domain.SpfCheckStatus)
        {
            case SpfCheckStatus.MissingRecord: yield return $"Publish an SPF record for {name}."; break;
            case SpfCheckStatus.MultipleRecords: yield return $"{name} has more than one SPF record. Merge them into one."; break;
            case SpfCheckStatus.Misconfigured: yield return $"Fix the SPF record for {name}. It has errors."; break;
            case SpfCheckStatus.TooManyLookups: yield return $"The SPF record for {name} needs more than 10 DNS lookups. Trim or flatten its includes."; break;
        }

        switch (domain.MxCheckStatus)
        {
            case MxCheckStatus.MissingRecord: yield return $"{name} has no MX record, so it can't receive mail. Publish one, or a null MX if it shouldn't."; break;
            case MxCheckStatus.UnresolvableTarget: yield return $"The mail server named in {name}'s MX record doesn't resolve. Fix the MX record."; break;
        }

        switch (domain.DkimCheckStatus)
        {
            case DkimCheckStatus.Missing: yield return $"A DKIM key for {name} is missing from DNS. Publish it again with the service that sends as {name}."; break;
            case DkimCheckStatus.Misconfigured: yield return $"A DKIM key for {name} is published incorrectly. Check it with the service that sends as {name}."; break;
        }

        switch (domain.TlsrptCheckStatus)
        {
            case TlsrptCheckStatus.MissingOwnRecord: yield return $"Publish a TLS reporting record for {name}, so delivery problems are reported."; break;
            case TlsrptCheckStatus.Misconfigured: yield return $"Fix the TLS reporting record for {name}."; break;
        }

        if (domain.MtaStsStatus == MtaStsStatus.Failed) yield return $"MTA-STS for {name} is failing. Check its policy host.";
        if (domain.MtaStsStatus == MtaStsStatus.PendingPolicyRecord) yield return $"MTA-STS for {name} needs its _mta-sts TXT record before senders will use it.";

        if (domain.DanglingCnameStatus == DanglingCnameStatus.Dangling) yield return $"{name} has subdomain records pointing at names that no longer exist, which someone could take over. Remove them.";

        switch (domain.CertificateStatus)
        {
            case CertificateCheckStatus.Expired: yield return $"The website certificate for {name} has expired, so visitors see a security warning. Renew it."; break;
            case CertificateCheckStatus.ExpiringSoon: yield return $"The website certificate for {name} expires within 14 days and hasn't renewed. Check its renewal."; break;
            case CertificateCheckStatus.Invalid: yield return $"The website certificate for {name} doesn't cover all its addresses (such as www), so some visitors see a security warning. Fix it with the web host."; break;
        }
    }
}
