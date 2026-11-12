using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Senders;

/// <summary>Reads and saves the optional AI provider used for source suggestions.</summary>
public static class SourceSuggestionSettingsService
{
    public static Task<SourceSuggestionSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.SourceSuggestionSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, SourceSuggestionSettings updated, string? newApiKey, CancellationToken cancellationToken = default)
    {
        Normalise(updated);
        var saved = await context.SourceSuggestionSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewKey = !string.IsNullOrEmpty(newApiKey);
        Validate(updated, saved.ApiKeyConfigured || hasNewKey);

        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Endpoint", saved.Endpoint, updated.Endpoint)
            .Field("Model", saved.Model, updated.Model)
            .Secret("API key", hasNewKey);
        if (!changes.Any)
        {
            return;
        }

        var existing = await context.SourceSuggestionSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.Endpoint = updated.Endpoint;
        existing.Model = updated.Model;
        if (hasNewKey)
        {
            await secretStore.SetSecretAsync(SourceSuggestionSettings.ApiKeySecretKey, newApiKey!, cancellationToken).ConfigureAwait(false);
            existing.ApiKeyConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.SourceSuggestionSettingsSaved, AuditTarget.Settings("AI suggestions"), "Saved AI suggestion settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Normalise(SourceSuggestionSettings settings)
    {
        settings.Endpoint = string.IsNullOrWhiteSpace(settings.Endpoint) ? null : settings.Endpoint.Trim().TrimEnd('/');
        settings.Model = string.IsNullOrWhiteSpace(settings.Model) ? null : settings.Model.Trim();
    }

    private static void Validate(SourceSuggestionSettings settings, bool willHaveKey)
    {
        if (settings.Endpoint is { Length: > 300 })
        {
            throw new ArgumentException("Endpoint can be at most 300 characters.", nameof(settings));
        }

        if (settings.Model is { Length: > 100 })
        {
            throw new ArgumentException("Model can be at most 100 characters.", nameof(settings));
        }

        if (!settings.Enabled)
        {
            if (settings.Endpoint is not null && EndpointProblem(settings.Endpoint) is { } problem)
            {
                throw new ArgumentException(problem, nameof(settings));
            }

            return;
        }

        if (settings.Endpoint is null)
        {
            throw new ArgumentException("Endpoint is required to turn suggestions on.", nameof(settings));
        }

        if (EndpointProblem(settings.Endpoint) is { } endpointProblem)
        {
            throw new ArgumentException(endpointProblem, nameof(settings));
        }

        if (settings.Model is null)
        {
            throw new ArgumentException("Model is required to turn suggestions on.", nameof(settings));
        }

        if (!willHaveKey)
        {
            throw new ArgumentException("An API key is required to turn suggestions on.", nameof(settings));
        }
    }

    /// <summary>An absolute http or https URL with no password in it. http is allowed because a local
    /// OpenAI-compatible server often has no TLS. The key is stored separately.</summary>
    internal static string? EndpointProblem(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
            ? null
            : "Endpoint must be an absolute http or https URL, without a password in it.";
}
