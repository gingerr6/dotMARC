using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.MtaSts;

/// <summary>One TLS report's figures for a domain's MTA-STS policy (policy type "sts").</summary>
public sealed record StsReportDay(DateTimeOffset BeginUtc, long Successful, long Failed);

/// <summary>Whether a domain in MTA-STS testing mode can move to enforce, and why not if it can't.</summary>
public sealed record MtaStsEnforceAssessment(bool Ready, string Summary);

/// <summary>A domain is ready to enforce MTA-STS once senders have reported on its policy for <see cref="DaysNeeded"/> days
/// with no failed connections in that time: in enforce mode, a sender refuses to deliver over a connection that fails
/// the policy, so failures in testing are mail that enforce would bounce.</summary>
public static class MtaStsEnforceReadiness
{
    public const int DaysNeeded = 14;

    /// <summary>Null when it doesn't apply: MTA-STS isn't hosted, live and in testing mode.</summary>
    public static MtaStsEnforceAssessment? Assess(Domain domain, IReadOnlyList<StsReportDay> reports, DateTimeOffset nowUtc)
    {
        if (!domain.MtaStsEnabled || domain.MtaStsStatus != MtaStsStatus.Active || domain.MtaStsMode != MtaStsMode.Testing)
        {
            return null;
        }

        var windowStart = nowUtc.AddDays(-DaysNeeded);
        var recent = reports.Where(report => report.BeginUtc >= windowStart).ToList();
        var successful = recent.Sum(report => report.Successful);
        var failed = recent.Sum(report => report.Failed);
        var daysOfReports = reports.Count == 0 ? 0 : Math.Min(DaysNeeded, (int)(nowUtc - reports.Min(report => report.BeginUtc)).TotalDays);

        if (failed > 0)
        {
            return new(false, $"Not ready to enforce: {failed:N0} connection(s) failed the policy in the last {DaysNeeded} days, and enforce would refuse them. See the TLS reports.");
        }
        if (daysOfReports < DaysNeeded || successful == 0)
        {
            return new(false, reports.Count == 0
                ? $"Not ready to enforce yet: no TLS reports about the policy have arrived. Senders send them daily once they've used it."
                : $"Not ready to enforce yet: {daysOfReports} of the {DaysNeeded} days of TLS reports needed, with no failures so far.");
        }

        return new(true, $"Ready to enforce: {DaysNeeded} days of TLS reports, {successful:N0} connection(s) checked against the policy and none failed.");
    }

    /// <summary>The domain's reports on its MTA-STS policy, back far enough to see when they started.</summary>
    public static async Task<List<StsReportDay>> LoadAsync(DotMarcDbContext context, int domainId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var since = nowUtc.AddDays(-DaysNeeded * 4);
        return await context.TlsrptReportPolicies.AsNoTracking()
            .Where(policy => policy.TlsrptReport.DomainId == domainId && policy.PolicyType == "sts" && policy.TlsrptReport.DateRangeBeginUtc >= since)
            .Select(policy => new StsReportDay(policy.TlsrptReport.DateRangeBeginUtc, policy.SuccessfulSessionCount, policy.FailedSessionCount))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}
