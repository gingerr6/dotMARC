using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;

namespace DotMarc.Senders;

/// <summary>Asks the configured model about one source and writes an audit entry. It never writes a
/// <see cref="SenderApproval"/>: the person still approves or marks the source not ours.</summary>
public static class SourceSuggestionService
{
    public static async Task<SourceSuggestionResult> SuggestAsync(DotMarcDbContext context, ISecretStore secrets, ISourceSuggestionClient client,
        AuditActor actor, Domain domain, SourceSuggestionFacts facts, CancellationToken cancellationToken = default)
    {
        if (!await SourceSuggestionAccess.MayAskAsync(context, actor, cancellationToken).ConfigureAwait(false))
        {
            return SourceSuggestionResult.Failed(SourceSuggestionAccess.RefusedMessage);
        }

        var settings = await SourceSuggestionSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        if (!settings.IsReady())
        {
            return SourceSuggestionResult.Failed("AI suggestions aren't configured. Nothing was changed.");
        }

        var apiKey = await secrets.GetSecretAsync(SourceSuggestionSettings.ApiKeySecretKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
        {
            return SourceSuggestionResult.Failed("AI suggestions aren't configured. Nothing was changed.");
        }

        var result = await client.SuggestAsync(settings.Endpoint!, settings.Model!, apiKey, facts, cancellationToken).ConfigureAwait(false);
        var summary = result.Verdict is { } verdict
            ? $"Asked for an AI suggestion for {facts.Ip} on {domain.Name}: {SourceSuggestionText.Label(verdict)}. {result.Reason}"
            : $"Asked for an AI suggestion for {facts.Ip} on {domain.Name}, and the provider didn't answer.";
        AuditLog.Record(context, actor, AuditActions.SourceSuggestionRequested, AuditTarget.For(domain), summary);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
