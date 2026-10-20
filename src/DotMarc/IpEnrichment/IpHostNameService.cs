using DotMarc.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DotMarc.IpEnrichment;

/// <summary>Caches reverse DNS names in IpHostName, as IpInfoService caches owner details: read with the caller's
/// context, written with a short-lived one of its own, since the Sources tab writes after its own context is gone.</summary>
public static class IpHostNameService
{
    /// <summary>A name is re-checked after a month, since providers renumber their senders. No name, or a lookup the
    /// resolver couldn't answer, is retried after a day, as IpInfoService does for a failed owner lookup.</summary>
    public static readonly TimeSpan FoundRetryWindow = TimeSpan.FromDays(30);
    public static readonly TimeSpan NotFoundRetryWindow = TimeSpan.FromHours(24);

    public static async Task<Dictionary<string, IpHostName>> GetCachedAsync(DotMarcDbContext context, IReadOnlyList<string> ips, CancellationToken cancellationToken) =>
        await context.IpHostNames
            .Where(h => ips.Contains(h.Ip))
            .ToDictionaryAsync(h => h.Ip, cancellationToken)
            .ConfigureAwait(false);

    public static bool NeedsLookup(IpHostName? cached, DateTimeOffset nowUtc) =>
        cached is null || cached.LookedUpUtc < nowUtc - (cached.HostName is null ? NotFoundRetryWindow : FoundRetryWindow);

    /// <summary>Looks up one IP's name and upserts its row. A lookup the resolver couldn't answer is stored as no name,
    /// so it's retried after NotFoundRetryWindow rather than on every page view.</summary>
    public static async Task<IpHostName> EnrichAsync(IDbContextFactory<DotMarcDbContext> dbFactory, IReverseDnsLookup lookup, string ip, CancellationToken cancellationToken)
    {
        string? hostName;
        try
        {
            hostName = await lookup.LookupAsync(ip, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            hostName = null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            hostName = null; // the HttpClient timed out
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await context.IpHostNames.SingleOrDefaultAsync(h => h.Ip == ip, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            existing = new IpHostName { Ip = ip };
            context.IpHostNames.Add(existing);
        }

        existing.HostName = hostName;
        existing.LookedUpUtc = DateTimeOffset.UtcNow;

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Another page view looked up the same IP at the same moment and saved first; use its result.
            context.ChangeTracker.Clear();
            existing = await context.IpHostNames.SingleAsync(h => h.Ip == ip, cancellationToken).ConfigureAwait(false);
        }

        return existing;
    }
}
