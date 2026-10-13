using System.Globalization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Psa.NinjaOne;

/// <summary>Reads and saves the single NinjaOne settings row, and connects and disconnects it. Changing what signs in
/// (instance, client ID or secret) resets the connection, because the saved refresh token belongs to the old app.</summary>
public static class NinjaOneSettingsService
{
    private static readonly AuditTarget Target = AuditTarget.Settings("NinjaOne");

    public static Task<NinjaOneSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.NinjaOneSettings.SingleAsync(cancellationToken);

    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, NinjaOneSettings updated, string? newClientSecret, CancellationToken cancellationToken = default)
    {
        updated.Instance = Trimmed(updated.Instance)?.ToLowerInvariant();
        updated.ClientId = Trimmed(updated.ClientId);
        if (updated.Instance is not null && NinjaOneOptions.Instances.All(instance => instance.Host != updated.Instance))
        {
            throw new ArgumentException($"{updated.Instance} isn't a NinjaOne instance. Choose one from the list.", nameof(updated));
        }

        RequireOneOf(updated.TicketType, NinjaOneOptions.TicketTypes, "ticket type");
        RequireOneOf(updated.Priority, NinjaOneOptions.Priorities, "priority");
        RequireOneOf(updated.Severity, NinjaOneOptions.Severities, "severity");

        // A no-tracking snapshot, because the caller may pass the very instance this context is tracking.
        var saved = await context.NinjaOneSettings.AsNoTracking().SingleAsync(cancellationToken).ConfigureAwait(false);
        var hasNewClientSecret = !string.IsNullOrWhiteSpace(newClientSecret);
        var changes = new AuditChanges()
            .Field("Enabled", saved.Enabled, updated.Enabled)
            .Field("Instance", saved.Instance, updated.Instance)
            .Field("Client ID", saved.ClientId, updated.ClientId)
            .Field("Ticket form", Describe(saved.TicketFormId, saved.TicketFormName), Describe(updated.TicketFormId, updated.TicketFormName))
            .Field("New ticket status", Describe(saved.NewStatusId, saved.NewStatusName), Describe(updated.NewStatusId, updated.NewStatusName))
            .Field("Type", saved.TicketType, updated.TicketType)
            .Field("Priority", saved.Priority, updated.Priority)
            .Field("Severity", saved.Severity, updated.Severity)
            .Secret("Client secret", hasNewClientSecret);
        if (!changes.Any)
        {
            return;
        }

        var signInChanged = saved.Instance != updated.Instance || saved.ClientId != updated.ClientId || hasNewClientSecret;
        var resetsConnection = signInChanged && saved.RefreshTokenConfigured;

        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.Enabled = updated.Enabled;
        existing.Instance = updated.Instance;
        existing.ClientId = updated.ClientId;
        (existing.TicketFormId, existing.TicketFormName) = (updated.TicketFormId, updated.TicketFormName);
        (existing.NewStatusId, existing.NewStatusName) = (updated.NewStatusId, updated.NewStatusName);
        existing.TicketType = updated.TicketType;
        existing.Priority = updated.Priority;
        existing.Severity = updated.Severity;

        if (hasNewClientSecret)
        {
            await secretStore.SetSecretAsync(NinjaOneSettings.ClientSecretKey, newClientSecret!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.ClientSecretConfigured = true;
        }

        if (resetsConnection)
        {
            await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, NinjaOneSettings.ClearedRefreshToken, cancellationToken).ConfigureAwait(false);
            ClearConnection(existing);
        }

        var summary = resetsConnection ? "Saved NinjaOne settings and reset the connection" : "Saved NinjaOne settings";
        AuditLog.Record(context, actor, AuditActions.NinjaOneSettingsSaved, Target, summary, changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stores the refresh token from a completed sign-in and marks NinjaOne connected.</summary>
    public static async Task ConnectAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, string refreshToken, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, refreshToken, cancellationToken).ConfigureAwait(false);
        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        existing.RefreshTokenConfigured = true;
        existing.ConnectedBy = actor.Name;
        existing.ConnectedUtc = nowUtc;
        existing.LastRefreshUtc = nowUtc;
        existing.ConnectionLostUtc = null;
        existing.ConnectionLostReason = null;
        AuditLog.Record(context, actor, AuditActions.NinjaOneConnected, Target, "Connected NinjaOne");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task DisconnectAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, CancellationToken cancellationToken = default)
    {
        await secretStore.SetSecretAsync(NinjaOneSettings.RefreshTokenKey, NinjaOneSettings.ClearedRefreshToken, cancellationToken).ConfigureAwait(false);
        var existing = await context.NinjaOneSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        ClearConnection(existing);
        AuditLog.Record(context, actor, AuditActions.NinjaOneDisconnected, Target, "Disconnected NinjaOne");
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ClearConnection(NinjaOneSettings settings)
    {
        settings.RefreshTokenConfigured = false;
        settings.ConnectedBy = null;
        settings.ConnectedUtc = null;
        settings.LastRefreshUtc = null;
        settings.ConnectionLostUtc = null;
        settings.ConnectionLostReason = null;
    }

    private static void RequireOneOf(string value, IReadOnlyList<string> allowed, string label)
    {
        if (!allowed.Contains(value))
        {
            throw new ArgumentException($"{value} isn't a NinjaOne {label}. Choose one of {string.Join(", ", allowed)}.");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A chosen option by its name, falling back to its id when the name wasn't saved.</summary>
    private static string? Describe(int? id, string? name) => id is null ? null : name ?? id.Value.ToString(CultureInfo.InvariantCulture);
}
