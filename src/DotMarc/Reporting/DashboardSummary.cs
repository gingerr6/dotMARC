using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Aggregate stats shown across the Dashboard's summary tiles, plus the per-domain table
/// rows derived from the same data. <see cref="Build"/> takes a fully-loaded domain list (Reports
/// already filtered to the report window by the caller's EF query) and a parse-failure count,
/// keeping this calculation testable without EF or Blazor - same "pure core, thin I/O adapter"
/// split as <see cref="DomainStatistics"/>.</summary>
public sealed record DashboardSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int ParseFailureCount, int SourceCount, ReasonBreakdown ReasonBreakdown)
{
    /// <summary>Which domains make up each count in <see cref="ReasonBreakdown"/>, most messages first, so the
    /// dashboard can say where they came from. Empty unless built by <see cref="Build"/>.</summary>
    public ReasonDomains ReasonDomains { get; init; } = ReasonDomains.Empty;

    public const string NotMonitored = "Not monitored";

    /// <summary>The dashboard's per-domain status: Not monitored when it's switched off, Missing when a monitored domain
    /// that sends mail has had no report for two days, Warning when its pass rate is under 95%, otherwise OK. Shared with
    /// the API's summary.</summary>
    public static string StatusOf(Domain domain, double? passRate)
    {
        if (!domain.IsMonitored)
        {
            return NotMonitored;
        }

        var isNullRouted = domain.SpfCheckStatus == SpfCheckStatus.NullSpf;
        var missingReport = domain.IsMonitored && !isNullRouted && (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
        return missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
    }

    public static (DashboardSummary Summary, List<DashboardDomainRow> Rows) Build(IReadOnlyList<Domain> domains, int parseFailureCount)
    {
        var rows = domains
            .OrderBy(d => d.SortOrder)
            .ThenBy(d => d.Name)
            .Select(d =>
            {
                var passRate = DomainStatistics.GetPassRate(d.Reports);

                var status = StatusOf(d, passRate);
                var color = status switch { "Missing" => Color.Error, "Warning" => Color.Warning, NotMonitored => Color.Default, _ => Color.Success };

                return new DashboardDomainRow(d.Id, d.Name, status, color, passRate, d.LastReportReceivedUtc, d.IsMonitored, d.DmarcCheckStatus, d.DmarcAuthorizationCheckStatus, d.MtaStsStatus, d.SpfCheckStatus) { MtaStsMode = d.MtaStsMode, Setup = DotMarc.Setup.DomainChecklist.Progress(d), Score = DotMarc.Scoring.SecurityScoreData.For(d, d.Reports) };
            })
            .ToList();

        var sourceCount = domains.SelectMany(d => d.Reports).Select(r => r.ReportingOrg).Distinct().Count();

        var summary = new DashboardSummary(
            rows.Count(r => r.IsMonitored),
            DomainStatistics.GetOverallPassRate(domains.Select(d => (IEnumerable<Report>)d.Reports)),
            rows.Count(r => r.Status == "Warning"),
            rows.Count(r => r.Status == "Missing"),
            parseFailureCount,
            sourceCount,
            DomainStatistics.GetReasonBreakdown(domains.Select(d => (IEnumerable<Report>)d.Reports)))
        {
            ReasonDomains = ReasonDomains.From(domains.Select(d => (d.Name, DomainStatistics.GetReasonBreakdown(d.Reports)))),
        };

        return (summary, rows);
    }
}

/// <summary>A domain and how many of its messages fall under one reject/quarantine reason.</summary>
public sealed record ReasonDomainCount(string DomainName, int Count);

/// <summary>Per reason in a <see cref="ReasonBreakdown"/>, the domains whose messages make up its count.</summary>
public sealed record ReasonDomains(
    IReadOnlyList<ReasonDomainCount> BenignOverride, IReadOnlyList<ReasonDomainCount> LocalPolicy, IReadOnlyList<ReasonDomainCount> Other,
    IReadOnlyList<ReasonDomainCount> InferredSpfFailure, IReadOnlyList<ReasonDomainCount> InferredDkimFailure, IReadOnlyList<ReasonDomainCount> InferredBothFailure,
    IReadOnlyList<ReasonDomainCount> NoReasonGiven)
{
    public static ReasonDomains Empty { get; } = new([], [], [], [], [], [], []);

    public static ReasonDomains From(IEnumerable<(string DomainName, ReasonBreakdown Breakdown)> perDomain)
    {
        var list = perDomain.Where(item => item.Breakdown.Total > 0).ToList();
        if (list.Count == 0)
        {
            return Empty;
        }

        IReadOnlyList<ReasonDomainCount> For(Func<ReasonBreakdown, int> count) => list
            .Where(item => count(item.Breakdown) > 0)
            .Select(item => new ReasonDomainCount(item.DomainName, count(item.Breakdown)))
            .OrderByDescending(item => item.Count).ThenBy(item => item.DomainName, StringComparer.Ordinal)
            .ToList();

        return new(For(b => b.BenignOverride), For(b => b.LocalPolicy), For(b => b.Other),
            For(b => b.InferredSpfFailure), For(b => b.InferredDkimFailure), For(b => b.InferredBothFailure), For(b => b.NoReasonGiven));
    }
}

/// <summary>One domain's row in the Dashboard's table.</summary>
public sealed record DashboardDomainRow(int Id, string Name, string Status, Color StatusColor, double? PassRate, DateTimeOffset? LastReportReceivedUtc, bool IsMonitored, DmarcCheckStatus DmarcCheckStatus, DmarcAuthorizationCheckStatus DmarcAuthorizationCheckStatus, MtaStsStatus MtaStsStatus, SpfCheckStatus SpfCheckStatus)
{
    public MtaStsMode MtaStsMode { get; init; }

    /// <summary>The domain's DNS setup checklist progress (see DotMarc.Setup.DomainChecklist).</summary>
    public (int Done, int Total) Setup { get; init; }

    /// <summary>The domain's security score (see DotMarc.Scoring.SecurityScore).</summary>
    public DotMarc.Scoring.DomainScore? Score { get; init; }
}
