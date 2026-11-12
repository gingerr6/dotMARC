using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Senders;

/// <summary>Who may ask for a source suggestion. The admin decides by the role on Manage access: the role includes
/// <see cref="Permission.SourceSuggestions"/> or it doesn't. Viewer and a new custom role don't, so a person is refused
/// until an admin adds it. Admin includes it, because the Admin role has every permission. The person's own switch is
/// a preference on top of this and is not checked here. An API key is refused even when its role includes the
/// permission (suggestions aren't part of the API). A client portal user is refused too: the portal doesn't offer
/// suggestions, and a client contact doesn't spend the provider.</summary>
public static class SourceSuggestionAccess
{
    public const string RefusedMessage = "You don't have permission to ask for AI suggestions. Nothing was changed.";

    public static async Task<bool> MayAskAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (actor.Kind != AuditActorKind.User)
        {
            return false;
        }

        var access = await FindGrantAsync(context, actor, cancellationToken).ConfigureAwait(false);
        if (access?.Role is null || access.IsClientPortal)
        {
            return false;
        }

        if (!access.Role.Permissions.Contains(Permission.SourceSuggestions))
        {
            return false;
        }

        // Same rule as sign-in: a grant that is actually limited to Groups loses a permission outside GroupLimits.
        var limitedToGroups = access.Role.IsScopable && access.ScopedGroups.Count > 0;
        return !limitedToGroups || GroupLimits.IsAllowed(Permission.SourceSuggestions);
    }

    /// <summary>The grant for this person, without binding an Entra object id. Binding is the sign-in path's job;
    /// asking for a suggestion must not write the grant.</summary>
    private static async Task<UserAccess?> FindGrantAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken)
    {
        IQueryable<UserAccess> Grants() => context.UserAccesses.AsNoTracking().Include(access => access.Role).Include(access => access.ScopedGroups).AsSplitQuery();

        if (!string.IsNullOrEmpty(actor.ObjectId))
        {
            var byObjectId = await Grants().SingleOrDefaultAsync(access => access.EntraObjectId == actor.ObjectId, cancellationToken).ConfigureAwait(false);
            if (byObjectId is not null)
            {
                return byObjectId;
            }
        }

        if (string.IsNullOrEmpty(actor.Email))
        {
            return null;
        }

        var email = actor.Email.Trim().ToLowerInvariant();
        return await Grants().SingleOrDefaultAsync(access => access.Email.ToLower() == email, cancellationToken).ConfigureAwait(false);
    }
}
