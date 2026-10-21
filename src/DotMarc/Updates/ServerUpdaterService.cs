using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Updates;

public static class ServerUpdaterService
{
    /// <summary>How long since the updater last checked in before it counts as not installed or not running. It checks
    /// every minute, so this allows for a slow backup during an install.</summary>
    public static readonly TimeSpan SeenWithin = TimeSpan.FromMinutes(5);

    public static Task<ServerUpdater> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.ServerUpdaters.AsNoTracking().SingleAsync(updater => updater.Id == 1, cancellationToken);

    public static bool IsRunning(ServerUpdater updater, DateTimeOffset nowUtc) =>
        updater.LastSeenUtc is { } seen && nowUtc - seen <= SeenWithin;

    /// <summary>Asks the server's updater to install the newest published image now, rather than at its next 15-minute check.</summary>
    public static async Task RequestAsync(DotMarcDbContext context, AuditActor actor, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var updater = await context.ServerUpdaters.SingleAsync(row => row.Id == 1, cancellationToken).ConfigureAwait(false);
        updater.RequestedUtc = nowUtc;
        updater.RequestedBy = actor.Name;
        AuditLog.Record(context, actor, AuditActions.ServerUpdateRequested, AuditTarget.Settings("Updates"), "Asked the server to install the newest version now");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
