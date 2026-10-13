using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotMarc.Psa.NinjaOne;

/// <summary>NinjaOne's refresh tokens last an hour unless NinjaOne support turns on sliding tokens, so dotMARC uses its
/// token at startup and every 20 minutes even when nothing is ticketing. This is also where the lost-connection alert is
/// raised and resolved. It follows the saved state, so a loss a ticket call found is raised on the next run too.</summary>
public sealed class NinjaOneKeepAliveService(IDbContextFactory<DotMarcDbContext> dbFactory, INinjaOneClient client, IAlertingService alerting, ILogger<NinjaOneKeepAliveService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "The NinjaOne keep-alive failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (settings.Enabled && settings.IsConnected)
        {
            try
            {
                await client.RefreshConnectionAsync(settings, cancellationToken).ConfigureAwait(false);
            }
            catch (NinjaOneConnectionLostException)
            {
                // The client has saved the loss; the alert follows below.
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(exception, "Refreshing the NinjaOne sign-in failed; trying again in {Interval}", Interval);
            }

            settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        }

        if (settings.Enabled && settings.RefreshTokenConfigured && settings.ConnectionLostUtc is not null)
        {
            await alerting.RaisePsaConnectionLostAsync(PsaKind.NinjaOne, settings.ConnectionLostReason ?? "NinjaOne refused the saved sign-in", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await alerting.ResolvePsaConnectionLostAsync(PsaKind.NinjaOne, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<NinjaOneSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await NinjaOneSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
    }
}
