using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Backups;

/// <summary>Runs the nightly offsite backup at the settings' hour. A run that fails is retried hourly until one
/// succeeds; a night missed while dotMARC was down is caught up when it starts.</summary>
public sealed class OffsiteBackupScheduler(IDbContextFactory<DotMarcDbContext> dbFactory, OffsiteBackupService backups, ILogger<OffsiteBackupScheduler> logger)
    : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        do
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken).ConfigureAwait(false);
                var settings = await BackupSettingsService.GetAsync(db, stoppingToken).ConfigureAwait(false);
                if (IsDue(settings, DateTimeOffset.UtcNow) && backups.WhyNot(settings) is null)
                {
                    await backups.BackUpAsync(stoppingToken, "scheduled").ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Checking whether an offsite backup is due failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>The next scheduled backup after the given time: the settings' hour (UTC), today or tomorrow.</summary>
    public static DateTimeOffset NextRunUtc(BackupSettings settings, DateTimeOffset afterUtc)
    {
        var next = new DateTimeOffset(afterUtc.UtcDateTime.Date.AddHours(settings.RunHourUtc), TimeSpan.Zero);
        return next > afterUtc ? next : next.AddDays(1);
    }

    /// <summary>Due when no backup has succeeded since the latest scheduled time, and nothing was tried in the last
    /// hour.</summary>
    public static bool IsDue(BackupSettings settings, DateTimeOffset nowUtc)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var scheduled = new DateTimeOffset(nowUtc.UtcDateTime.Date.AddHours(settings.RunHourUtc), TimeSpan.Zero);
        if (scheduled > nowUtc)
        {
            scheduled = scheduled.AddDays(-1);
        }

        return (settings.LastSuccessUtc is null || settings.LastSuccessUtc < scheduled)
            && (settings.LastRunUtc is null || settings.LastRunUtc < scheduled || settings.LastRunUtc <= nowUtc - RetryInterval);
    }
}
