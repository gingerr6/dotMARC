using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Senders;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Enforcement;

/// <summary>Loads what EnforcementReadiness needs for a set of domains in a few queries: each sender's mail over the last
/// EnforcementReadiness.DaysNeeded days (recognised services grouped, from cached host names and owners), the domains'
/// sender decisions, and when each first reported.</summary>
public static class EnforcementData
{
    public static async Task<Dictionary<int, EnforcementAssessment>> AssessAsync(DotMarcDbContext db, IReadOnlyList<Domain> domains, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var domainIds = domains.Select(domain => domain.Id).ToList();
        var windowStart = nowUtc.AddDays(-EnforcementReadiness.DaysNeeded);

        var perIp = await db.ReportRecords.AsNoTracking()
            .Where(record => domainIds.Contains(record.Report.DomainId) && record.Report.ReceivedUtc >= windowStart)
            .GroupBy(record => new { record.Report.DomainId, record.SourceIp })
            .Select(group => new
            {
                group.Key.DomainId,
                group.Key.SourceIp,
                Volume = group.Sum(record => record.MessageCount),
                Passing = group.Where(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass).Sum(record => record.MessageCount),
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var firstReports = await db.Reports.AsNoTracking()
            .Where(report => domainIds.Contains(report.DomainId))
            .GroupBy(report => report.DomainId)
            .Select(group => new { DomainId = group.Key, First = group.Min(report => report.ReceivedUtc) })
            .ToDictionaryAsync(row => row.DomainId, row => row.First, cancellationToken).ConfigureAwait(false);

        var approvals = (await db.SenderApprovals.AsNoTracking().Where(approval => domainIds.Contains(approval.DomainId)).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(approval => approval.DomainId);

        var ips = perIp.Select(row => row.SourceIp).Distinct().ToList();
        var hostNames = await IpHostNameService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);
        var owners = await IpInfoService.GetCachedAsync(db, ips, cancellationToken).ConfigureAwait(false);
        var ranges = await IpInfoService.GetCachedRangesAsync(db, cancellationToken).ConfigureAwait(false);

        var sendersByDomain = perIp
            .GroupBy(row => row.DomainId)
            .ToDictionary(group => group.Key, group => group
                .Select(row => (Row: row, Identity: SenderIdentity.For(row.SourceIp, hostNames.GetValueOrDefault(row.SourceIp)?.HostName,
                    owners.GetValueOrDefault(row.SourceIp)?.Organization ?? IpRangeMatcher.FindContaining(ranges, row.SourceIp)?.Organization)))
                .GroupBy(item => item.Identity.Key)
                .Select(sender => new SenderVolume(sender.First().Identity, sender.Sum(item => item.Row.Volume), sender.Sum(item => item.Row.Passing)))
                .ToList());

        return domains.ToDictionary(domain => domain.Id, domain => EnforcementReadiness.Assess(
            domain,
            sendersByDomain.GetValueOrDefault(domain.Id) ?? [],
            approvals[domain.Id].ToDictionary(approval => approval.SenderKey, approval => approval.Status),
            firstReports.TryGetValue(domain.Id, out var first) ? first : null,
            nowUtc));
    }
}
