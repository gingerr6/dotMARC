using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Senders;

/// <summary>Who may ask for a source suggestion. Any signed-in app user may, once an admin has connected a provider.
/// That is not a role permission and not a personal switch: Viewer, a custom role, and a person limited to Groups
/// may all ask. An API key is refused (suggestions aren't part of the API). A client portal user is refused too:
/// the portal doesn't offer suggestions, and a client contact doesn't spend the provider.</summary>
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
        return access?.Role is not null && !access.IsClientPortal;
    }

    /// <summary>The grant for this person, without binding an Entra object id. Binding is the sign-in path's job;
    /// asking for a suggestion must not write the grant.</summary>
    private static async Task<UserAccess?> FindGrantAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken)
    {
        IQueryable<UserAccess> Grants() => context.UserAccesses.AsNoTracking().Include(access => access.Role);

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
