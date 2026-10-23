using DotMarc.Data;
using DotMarc.MtaSts;
using Xunit;

namespace DotMarc.Tests.MtaSts;

public sealed class MtaStsEnforceReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static Domain Testing() => new()
    {
        Name = "bcdr.co.uk", MtaStsEnabled = true, MtaStsStatus = MtaStsStatus.Active, MtaStsMode = MtaStsMode.Testing,
    };

    /// <summary>One report a day for the given number of days back, each with these counts.</summary>
    private static List<StsReportDay> Daily(int days, long successful = 2, long failed = 0) =>
        Enumerable.Range(1, days).Select(day => new StsReportDay(Now.AddDays(-day), successful, failed)).ToList();

    [Fact]
    public void Fourteen_clean_days_is_ready()
    {
        var result = MtaStsEnforceReadiness.Assess(Testing(), Daily(20), Now);

        Assert.True(result!.Ready);
    }

    [Fact]
    public void Fewer_than_fourteen_days_is_not_ready_yet()
    {
        var result = MtaStsEnforceReadiness.Assess(Testing(), Daily(5), Now);

        Assert.False(result!.Ready);
        Assert.Contains("5 of the 14 days", result.Summary);
    }

    [Fact]
    public void A_failure_in_the_last_fourteen_days_is_not_ready()
    {
        var reports = Daily(20);
        reports.Add(new StsReportDay(Now.AddDays(-3), 0, 4));

        var result = MtaStsEnforceReadiness.Assess(Testing(), reports, Now);

        Assert.False(result!.Ready);
        Assert.Contains("4 connection(s) failed", result.Summary);
    }

    [Fact]
    public void An_old_failure_before_the_window_doesnt_count()
    {
        var reports = Daily(20);
        reports.Add(new StsReportDay(Now.AddDays(-18), 0, 4));

        Assert.True(MtaStsEnforceReadiness.Assess(Testing(), reports, Now)!.Ready);
    }

    [Fact]
    public void It_doesnt_apply_outside_testing_mode()
    {
        var domain = Testing();
        domain.MtaStsMode = MtaStsMode.Enforce;

        Assert.Null(MtaStsEnforceReadiness.Assess(domain, Daily(20), Now));
    }
}
