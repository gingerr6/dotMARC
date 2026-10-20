using DotMarc.Backups;
using Xunit;

namespace DotMarc.Tests.Backups;

public sealed class OffsiteBackupSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private static BackupSettings Settings(DateTimeOffset? lastRun = null, DateTimeOffset? lastSuccess = null, bool enabled = true) =>
        new() { Enabled = enabled, RunHourUtc = 3, LastRunUtc = lastRun, LastSuccessUtc = lastSuccess };

    [Fact]
    public void IsDue_WhenNothingHasRunSinceTheScheduledHour()
    {
        Assert.True(OffsiteBackupScheduler.IsDue(Settings(), Now));
        Assert.True(OffsiteBackupScheduler.IsDue(Settings(Now.AddHours(-7), Now.AddHours(-7)), Now)); // 02:00 today, before 03:00
    }

    [Fact]
    public void IsNotDue_OnceTodaysHasSucceeded_OrWhenOff()
    {
        Assert.False(OffsiteBackupScheduler.IsDue(Settings(Now.AddHours(-5), Now.AddHours(-5)), Now)); // 04:00 today
        Assert.False(OffsiteBackupScheduler.IsDue(Settings(enabled: false), Now));
    }

    [Fact]
    public void AFailedRun_IsRetriedAfterAnHour()
    {
        Assert.False(OffsiteBackupScheduler.IsDue(Settings(lastRun: Now.AddMinutes(-30), lastSuccess: Now.AddDays(-1)), Now));
        Assert.True(OffsiteBackupScheduler.IsDue(Settings(lastRun: Now.AddMinutes(-61), lastSuccess: Now.AddDays(-1)), Now));
    }

    [Fact]
    public void BeforeTheHour_YesterdaysScheduleCounts()
    {
        var early = new DateTimeOffset(2026, 10, 20, 1, 0, 0, TimeSpan.Zero);
        Assert.False(OffsiteBackupScheduler.IsDue(Settings(early.AddHours(-20), early.AddHours(-20)), early)); // 05:00 yesterday
        Assert.True(OffsiteBackupScheduler.IsDue(Settings(early.AddDays(-2), early.AddDays(-2)), early));
    }
}
