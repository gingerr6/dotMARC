using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Senders;

/// <summary>Reads and writes <see cref="UserPreference"/>. Each person is independent: turning suggestions on for one
/// does not turn them on for anyone else, and a person with no row has them off. This is only the personal switch.
/// Their role still has to include SourceSuggestions before the switch or the Suggest button is shown.</summary>
public static class UserPreferenceService
{
    public static string KeyFor(AuditActor actor) =>
        !string.IsNullOrEmpty(actor.ObjectId) ? "oid:" + actor.ObjectId
        : !string.IsNullOrEmpty(actor.Email) ? "email:" + actor.Email.Trim().ToLowerInvariant()
        : "name:" + actor.Name;

    public static async Task<bool> SourceSuggestionsEnabledAsync(DotMarcDbContext context, AuditActor actor, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(actor);
        return await context.UserPreferences.AsNoTracking()
            .Where(preference => preference.UserKey == key)
            .Select(preference => preference.SourceSuggestionsEnabled)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task SetSourceSuggestionsEnabledAsync(DotMarcDbContext context, AuditActor actor, bool enabled, CancellationToken cancellationToken = default)
    {
        var key = KeyFor(actor);
        var existing = await context.UserPreferences.SingleOrDefaultAsync(preference => preference.UserKey == key, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            if (!enabled)
            {
                return;
            }

            context.UserPreferences.Add(new UserPreference { UserKey = key, SourceSuggestionsEnabled = true });
        }
        else
        {
            existing.SourceSuggestionsEnabled = enabled;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
