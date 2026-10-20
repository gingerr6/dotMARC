using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Backups;

public static class BackupSettingsService
{
    public static Task<BackupSettings> GetAsync(DotMarcDbContext context, CancellationToken cancellationToken = default) =>
        context.BackupSettings.SingleAsync(cancellationToken);

    /// <summary>Saves the settings; a blank new secret keeps the saved one. The run status fields aren't touched.</summary>
    public static async Task SaveAsync(DotMarcDbContext context, AuditActor actor, ISecretStore secretStore, BackupSettings updated,
        string? newSecretAccessKey, string? newPassphrase, CancellationToken cancellationToken = default)
    {
        var bucket = Clean(updated.Bucket);
        var region = Clean(updated.Region);
        var prefix = (Clean(updated.Prefix) ?? "dotmarc-backups").Trim('/');
        var endpoint = Clean(updated.EndpointUrl);
        var accessKeyId = Clean(updated.AccessKeyId);
        var runHour = Math.Clamp(updated.RunHourUtc, 0, 23);
        var retention = Math.Clamp(updated.RetentionDays, 1, 3650);
        var hasNewSecret = !string.IsNullOrWhiteSpace(newSecretAccessKey);
        var hasNewPassphrase = !string.IsNullOrWhiteSpace(newPassphrase);

        var existing = await context.BackupSettings.SingleAsync(cancellationToken).ConfigureAwait(false);
        var changes = new AuditChanges()
            .Field("Enabled", existing.Enabled, updated.Enabled)
            .Field("Bucket", existing.Bucket, bucket)
            .Field("Region", existing.Region, region)
            .Field("Prefix", existing.Prefix, prefix)
            .Field("Endpoint", existing.EndpointUrl, endpoint)
            .Field("Access key ID", existing.AccessKeyId, accessKeyId)
            .Secret("Secret access key", hasNewSecret)
            .Secret("Passphrase", hasNewPassphrase)
            .Field("Hour (UTC)", existing.RunHourUtc, runHour)
            .Field("Days kept", existing.RetentionDays, retention);
        if (!changes.Any)
        {
            return;
        }

        existing.Enabled = updated.Enabled;
        existing.Bucket = bucket;
        existing.Region = region;
        existing.Prefix = prefix;
        existing.EndpointUrl = endpoint;
        existing.AccessKeyId = accessKeyId;
        existing.RunHourUtc = runHour;
        existing.RetentionDays = retention;
        if (hasNewSecret)
        {
            await secretStore.SetSecretAsync(BackupSettings.SecretAccessKeyStoreKey, newSecretAccessKey!.Trim(), cancellationToken).ConfigureAwait(false);
            existing.SecretAccessKeyConfigured = true;
        }

        if (hasNewPassphrase)
        {
            await secretStore.SetSecretAsync(BackupSettings.PassphraseStoreKey, newPassphrase!, cancellationToken).ConfigureAwait(false);
            existing.PassphraseConfigured = true;
        }

        AuditLog.Record(context, actor, AuditActions.BackupSettingsSaved, AuditTarget.Settings("Backups"), "Saved backup settings", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
