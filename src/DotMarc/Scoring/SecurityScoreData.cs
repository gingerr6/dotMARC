using DotMarc.Data;
using DotMarc.Reporting;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Scoring;

/// <summary>Works out domains' scores from reports already loaded, or straight from the database for pages that don't
/// load reports (Groups, Client reports).</summary>
public static class SecurityScoreData
{
    public static SecurityScore.ReportRates RatesFrom(IEnumerable<Report> reportsInWindow)
    {
        var records = reportsInWindow.SelectMany(report => report.Records).ToList();
        var total = records.Sum(record => record.MessageCount);
        return total == 0
            ? new(null, null)
            : new((double)records.Where(DomainStatistics.IsPassing).Sum(record => record.MessageCount) / total,
                (double)records.Where(record => record.DkimResult == AuthResult.Pass).Sum(record => record.MessageCount) / total);
    }

    public static DomainScore For(Domain domain, IEnumerable<Report> reportsInWindow) => SecurityScore.For(domain, RatesFrom(reportsInWindow));

    /// <summary>Scores for the given domains, with their pass rates summed in the database over the report window.</summary>
    public static async Task<Dictionary<int, DomainScore>> ForDomainsAsync(DotMarcDbContext db, IReadOnlyList<Domain> domains, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var ids = domains.Select(domain => domain.Id).ToList();
        var cutoff = DomainStatistics.GetWindowCutoffUtc(nowUtc);
        var totals = await db.ReportRecords.AsNoTracking()
            .Where(record => ids.Contains(record.Report.DomainId) && record.Report.ReceivedUtc >= cutoff)
            .GroupBy(record => record.Report.DomainId)
            .Select(group => new
            {
                DomainId = group.Key,
                Total = group.Sum(record => record.MessageCount),
                Passing = group.Where(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass).Sum(record => record.MessageCount),
                DkimPassing = group.Where(record => record.DkimResult == AuthResult.Pass).Sum(record => record.MessageCount),
            })
            .ToDictionaryAsync(row => row.DomainId, cancellationToken).ConfigureAwait(false);

        return domains.ToDictionary(domain => domain.Id, domain =>
            SecurityScore.For(domain, totals.TryGetValue(domain.Id, out var row) && row.Total > 0
                ? new SecurityScore.ReportRates((double)row.Passing / row.Total, (double)row.DkimPassing / row.Total)
                : new SecurityScore.ReportRates(null, null)));
    }

    /// <summary>Each Group's score: the average of its monitored domains' scores. A Group with none has no score.</summary>
    public static async Task<Dictionary<int, int?>> ForGroupsAsync(DotMarcDbContext db, IReadOnlyCollection<int> groupIds, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default)
    {
        var domains = await db.Domains.AsNoTracking()
            .Where(domain => domain.IsMonitored && domain.Groups.Any(group => groupIds.Contains(group.Id)))
            .Include(domain => domain.Groups)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var scores = await ForDomainsAsync(db, domains, nowUtc, cancellationToken).ConfigureAwait(false);
        return groupIds.ToDictionary(id => id, id => SecurityScore.Average(domains.Where(domain => domain.Groups.Any(group => group.Id == id)).Select(domain => scores[domain.Id])));
    }
}
