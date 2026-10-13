using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Writes the connection's health to the settings row. The client records here, not on the alerting service,
/// because the alerting service already depends on the PSA providers. NinjaOneKeepAliveService turns the saved state
/// into the alert.</summary>
public interface INinjaOneConnectionRecorder
{
    Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default);
    Task RefreshedAsync(CancellationToken cancellationToken = default);
}

public sealed class NinjaOneConnectionRecorder(IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider) : INinjaOneConnectionRecorder
{
    private static readonly AuditActor KeepAliveActor = AuditActor.ForSystem("NinjaOne keep-alive");

    public async Task ConnectionLostAsync(string reason, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.RefreshTokenConfigured || settings.ConnectionLostUtc is not null)
        {
            return;
        }

        settings.ConnectionLostUtc = timeProvider.GetUtcNow();
        settings.ConnectionLostReason = reason.Length <= 500 ? reason : reason[..500];
        AuditLog.Record(context, KeepAliveActor, AuditActions.NinjaOneConnectionLost, AuditTarget.Settings("NinjaOne"), $"Lost the NinjaOne connection: {settings.ConnectionLostReason}");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshedAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        settings.LastRefreshUtc = timeProvider.GetUtcNow();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
