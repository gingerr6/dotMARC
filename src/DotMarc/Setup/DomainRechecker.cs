using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.DnsPush;
using DotMarc.Graph;
using DotMarc.Ingestion;
using DotMarc.MtaSts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DotMarc.Setup;

/// <summary>Runs every DNS check on a domain now, rather than on its schedule: for a checklist straight after records are
/// added, or for domains just added. Each check is PollingService's own per-domain check, as the domain page's recheck
/// buttons use, so this can't drift from the scheduled checks. A check that fails is left as it was.</summary>
public sealed class DomainRechecker(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    IDmarcDnsChecker dmarcChecker,
    ITlsrptDnsChecker tlsrptChecker,
    ISpfDnsChecker spfChecker,
    IMxDnsChecker mxChecker,
    IDkimDnsChecker dkimChecker,
    IDnsProviderDetector dnsProviderDetector,
    IMtaStsDnsVerifier mtaStsDnsVerifier,
    IMtaStsServingVerifier mtaStsServingVerifier,
    IMtaStsHostProvisioner mtaStsHostProvisioner,
    IOptions<GraphOptions> graphOptions,
    IOptions<MtaStsOptions> mtaStsOptions,
    ILogger<DomainRechecker> logger)
{
    public async Task RecheckAsync(IReadOnlyList<int> domainIds, CancellationToken cancellationToken = default)
    {
        foreach (var domainId in domainIds)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var domain = await db.Domains.Include(d => d.DkimRecords).SingleOrDefaultAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
            if (domain is null)
            {
                continue;
            }

            var mailbox = graphOptions.Value.MailboxAddress;
            await TryAsync(domain, "DMARC", () => PollingService.RunSingleDmarcCheckAsync(domain, dmarcChecker, mailbox, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "DMARC authorization", () => PollingService.RunSingleDmarcAuthorizationCheckAsync(domain, dmarcChecker, mailbox, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "SPF", () => PollingService.RunSingleSpfCheckAsync(domain, spfChecker, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "MX", () => PollingService.RunSingleMxCheckAsync(domain, mxChecker, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "DKIM", () => PollingService.RunSingleDkimCheckAsync(domain, dkimChecker, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "TLS reporting", () => PollingService.RunSingleTlsrptCheckAsync(domain, tlsrptChecker, graphOptions.Value.TlsReportingAddress, cancellationToken)).ConfigureAwait(false);
            await TryAsync(domain, "DNS provider", () => PollingService.RunSingleDnsProviderCheckAsync(domain, dnsProviderDetector, cancellationToken)).ConfigureAwait(false);
            if (domain.MtaStsEnabled && mtaStsOptions.Value.HostingHostname is { Length: > 0 } hostingHostname)
            {
                await TryAsync(domain, "MTA-STS", () => PollingService.RunSingleMtaStsCheckAsync(domain, mtaStsDnsVerifier, mtaStsServingVerifier, mtaStsHostProvisioner, hostingHostname, cancellationToken)).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryAsync(Domain domain, string check, Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Checking {Check} for {Domain} failed; leaving its last result.", check, domain.Name);
        }
    }
}
