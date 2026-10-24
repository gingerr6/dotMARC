using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Lookalikes;

/// <summary>Keeps a domain's lookalikes (see LookalikeDomain) up to date from a check, works out its status from them,
/// and records someone dismissing one, audited like other domain edits.</summary>
public static class LookalikeService
{
    /// <summary>Checks the domain's lookalikes and stores what's registered: new ones added, ones no longer registered
    /// removed, dismissals kept. The caller saves.</summary>
    public static async Task RefreshAsync(DotMarcDbContext context, Domain domain, ILookalikeChecker checker, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var candidates = LookalikeGenerator.For(domain.Name);
        if (candidates.Count == 0)
        {
            domain.LookalikeStatus = LookalikeStatus.NotApplicable;
            domain.LookalikeCheckedUtc = nowUtc;
            domain.LookalikeDetail = "Only checked for a registered domain, not a subdomain.";
            return;
        }

        var registered = await checker.FindRegisteredAsync(candidates, cancellationToken).ConfigureAwait(false);
        var stored = await context.LookalikeDomains.Where(lookalike => lookalike.DomainId == domain.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byName = stored.ToDictionary(lookalike => lookalike.Name, StringComparer.Ordinal);
        foreach (var found in registered)
        {
            if (byName.Remove(found.Name, out var existing))
            {
                existing.HasMx = found.HasMx;
                existing.LastSeenUtc = nowUtc;
            }
            else
            {
                context.LookalikeDomains.Add(new LookalikeDomain { DomainId = domain.Id, Name = found.Name, Kind = found.Kind, HasMx = found.HasMx, FirstSeenUtc = nowUtc, LastSeenUtc = nowUtc });
            }
        }
        context.LookalikeDomains.RemoveRange(byName.Values);

        var current = registered.Select(found => (found.Name, found.HasMx, Dismissed: stored.FirstOrDefault(row => row.Name == found.Name)?.Dismissed ?? false)).ToList();
        ApplyStatus(domain, current, candidates.Count);
        domain.LookalikeCheckedUtc = nowUtc;
    }

    /// <summary>Marks a lookalike known (or not), and updates the domain's status to match.</summary>
    public static async Task SetDismissedAsync(DotMarcDbContext context, AuditActor actor, int lookalikeId, bool dismissed, CancellationToken cancellationToken = default)
    {
        var lookalike = await context.LookalikeDomains.SingleAsync(row => row.Id == lookalikeId, cancellationToken).ConfigureAwait(false);
        if (lookalike.Dismissed == dismissed)
        {
            return;
        }

        var domain = await context.Domains.SingleAsync(d => d.Id == lookalike.DomainId, cancellationToken).ConfigureAwait(false);
        lookalike.Dismissed = dismissed;
        var rows = await context.LookalikeDomains.Where(row => row.DomainId == domain.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        ApplyStatus(domain, rows.Select(row => (row.Name, row.HasMx, row.Dismissed)).ToList(), LookalikeGenerator.For(domain.Name).Count);

        AuditLog.Record(context, actor, AuditActions.DomainLookalikeReviewed, AuditTarget.For(domain),
            $"{(dismissed ? "Dismissed" : "Restored")} lookalike {lookalike.Name} for {domain.Name}",
            new AuditChanges().Field("Dismissed", !dismissed, dismissed));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void ApplyStatus(Domain domain, IReadOnlyList<(string Name, bool HasMx, bool Dismissed)> registered, int checkedCount)
    {
        var withMail = registered.Where(row => row.HasMx && !row.Dismissed).Select(row => row.Name).ToList();
        domain.LookalikeStatus = withMail.Count > 0 ? LookalikeStatus.Found : LookalikeStatus.Clear;
        domain.LookalikeDetail = withMail.Count > 0
            ? $"{withMail.Count} lookalike domain(s) set up to receive mail, which is how invoice fraud starts: {string.Join(", ", withMail.Take(5))}{(withMail.Count > 5 ? " and more" : "")}. Check them, and dismiss the ones you know about."
            : registered.Count == 0
                ? $"None of the {checkedCount} typo and look-alike variants checked is registered."
                : $"{registered.Count} of the {checkedCount} variants checked are registered, but none that can receive mail{(registered.Any(row => row.Dismissed) ? " and hasn't been dismissed" : "")}.";
    }
}
