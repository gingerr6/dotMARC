using DotMarc.Bimi;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.MtaSts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.DnsPush;

/// <summary>Publishes default._bimi.&lt;domain&gt; pointing at the logo (and certificate) dotMARC hosts for the domain.</summary>
public sealed class BimiChangeBuilder(ITxtRecordLookup txtLookup, IDbContextFactory<DotMarcDbContext> dbFactory, IOptions<MtaStsOptions> mtaStsOptions) : IDnsChangeBuilder
{
    public string Target => "bimi";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        var hostingHostname = mtaStsOptions.Value.HostingHostname;
        if (string.IsNullOrWhiteSpace(hostingHostname))
        {
            return DnsChangePlan.Refuse("error");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var asset = await db.BimiAssets.AsNoTracking().SingleOrDefaultAsync(row => row.DomainId == request.Domain.Id, cancellationToken).ConfigureAwait(false);
        if (asset is null)
        {
            return DnsChangePlan.Refuse("error");
        }

        var name = $"default._bimi.{request.Domain.Name}";
        var desired = BimiHosting.Record(hostingHostname, request.Domain.Name, asset.CertificatePem is not null);
        var existing = await txtLookup.LookupWithCnameAsync(name, cancellationToken).ConfigureAwait(false);
        if (existing.DelegatedToCname is not null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Replace, "TXT", name, desired, existing.DelegatedToCname, request.DomainZone!, ExistingRecordType: "CNAME"));
        }

        if (existing.DirectValue is null)
        {
            return DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Create, "TXT", name, desired, null, request.DomainZone!));
        }

        return string.Equals(existing.DirectValue.Trim(), desired, StringComparison.Ordinal)
            ? DnsChangePlan.Of()
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", name, desired, existing.DirectValue, request.DomainZone!));
    }
}
