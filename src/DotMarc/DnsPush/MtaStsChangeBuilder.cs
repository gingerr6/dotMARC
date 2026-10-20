using DotMarc.Dns;
using DotMarc.MtaSts;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

public sealed class MtaStsChangeBuilder(
    IOptions<MtaStsOptions> mtaStsOptions, IMtaStsCnameLookup mtaStsCnameLookup, IMtaStsHostProvisioner mtaStsHostProvisioner, ITxtRecordLookup txtRecordLookup)
    : IDnsChangeBuilder
{
    public string Target => "mta-sts";
    public string RequiredPolicy => "MtaStsManage";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrEmpty(hostingHostname))
        {
            return DnsChangePlan.Refuse("error");
        }

        var domainName = request.Domain.Name;
        var domainZone = request.DomainZone!;
        var existingCname = await mtaStsCnameLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false);
        var cnameChange = existingCname is null
            ? new DnsRecordChange(DnsRecordChangeKind.Create, "CNAME", $"mta-sts.{domainName}", hostingHostname, null, domainZone)
            : new DnsRecordChange(DnsRecordChangeKind.Merge, "CNAME", $"mta-sts.{domainName}", hostingHostname, existingCname, domainZone);
        var changes = new List<DnsRecordChange> { cnameChange };

        // The _mta-sts TXT record that tells senders there's a policy to fetch, with an id that changes with the policy.
        var domain = request.Domain;
        var policyRecord = MtaStsPolicyRenderer.PolicyRecord(MtaStsPolicyRenderer.PolicyId(domain.MtaStsMode, domain.MtaStsMxHosts, domain.MtaStsMaxAgeSeconds));
        var existingRecord = (await txtRecordLookup.GetTxtValuesAsync($"_mta-sts.{domainName}", cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(value => value.TrimStart().StartsWith("v=STSv1", StringComparison.Ordinal));
        if (existingRecord is null)
        {
            changes.Add(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"_mta-sts.{domainName}", policyRecord, null, domainZone));
        }
        else if (existingRecord.Trim() != policyRecord)
        {
            changes.Add(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_mta-sts.{domainName}", policyRecord, existingRecord, domainZone));
        }

        // Azure Container Apps also needs a domain-ownership TXT record before it will bind the
        // custom domain - see AzureMtaStsHostProvisioner and the design spec's "Fetching the
        // verification ID" section. Caddy has no such requirement, and a null/empty ID (the ARM
        // call failed, or this deployment isn't actually Azure-provisioned) just means the push
        // proceeds with the CNAME alone rather than failing outright.
        if (string.Equals(mtaStsOptions.Value.Provisioner, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            var verificationId = await mtaStsHostProvisioner.GetDomainVerificationIdAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(verificationId))
            {
                var existingAsuid = await mtaStsCnameLookup.LookupAsuidTxtAsync(domainName, cancellationToken).ConfigureAwait(false);
                changes.Add(existingAsuid is null
                    ? new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", $"asuid.mta-sts.{domainName}", verificationId, null, domainZone)
                    : new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"asuid.mta-sts.{domainName}", verificationId, existingAsuid, domainZone));
            }
        }

        return new DnsChangePlan(changes, null);
    }
}
