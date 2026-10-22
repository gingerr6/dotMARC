using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Backups;

public sealed record BackupOutcome(bool Succeeded, string Message, string? Key = null, long? Size = null);

/// <summary>Encrypted offsite backups of dotMARC: the database (which also holds the keys that decrypt every stored
/// secret) and the settings dotMARC was started with, tarred, encrypted with the passphrase (see OpenSslEncryption)
/// before they leave the server, and uploaded to S3. Run nightly by OffsiteBackupScheduler, or from the Backups page,
/// which also restores them. One backup or restore runs at a time.</summary>
public sealed class OffsiteBackupService(
    IDbContextFactory<DotMarcDbContext> dbFactory,
    ISecretStore secretStore,
    IBackupStoreFactory storeFactory,
    IDatabaseDumper dumper,
    IAlertingService alerting,
    BackupNotifier notifier,
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<OffsiteBackupService> logger)
{
    public const string FilePrefix = "dotmarc-backup-";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsBusy => _gate.CurrentCount == 0;

    /// <summary>Why backups can't run with these settings, or null when they can.</summary>
    public string? WhyNot(BackupSettings settings) =>
        dumper.Unavailable ?? (settings.IsComplete ? null : "Fill in the bucket, region, access keys and passphrase first.");

    /// <summary>Takes a backup now. Trigger says who asked, for its email ("scheduled" or "started by hand").</summary>
    public async Task<BackupOutcome> BackUpAsync(CancellationToken cancellationToken = default, string trigger = "started by hand")
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BackupOutcome(false, "A backup or restore is already running.");
        }

        try
        {
            return await BackUpCoreAsync(suffix: "", trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<StoredBackup>> ListAsync(CancellationToken cancellationToken = default)
    {
        var (settings, store, _) = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var items = await store.ListAsync(settings.Prefix + "/", cancellationToken).ConfigureAwait(false);
        return items.Where(item => item.FileName.StartsWith(FilePrefix, StringComparison.Ordinal)).OrderByDescending(item => item.LastModifiedUtc).ToList();
    }

    /// <summary>Checks the bucket can be written, listed and deleted from, with a small test object.</summary>
    public async Task<BackupOutcome> TestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var (settings, store, _) = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var key = $"{settings.Prefix}/connection-test-{Guid.NewGuid():N}.txt";
            var file = Path.GetTempFileName();
            try
            {
                await File.WriteAllTextAsync(file, "dotMARC connection test", cancellationToken).ConfigureAwait(false);
                await store.UploadAsync(key, file, cancellationToken).ConfigureAwait(false);
                var count = (await store.ListAsync(settings.Prefix + "/", cancellationToken).ConfigureAwait(false))
                    .Count(item => item.FileName.StartsWith(FilePrefix, StringComparison.Ordinal));
                await store.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
                return new BackupOutcome(true, $"Connected. dotMARC can write to the bucket, and it holds {count} backup(s).");
            }
            finally
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new BackupOutcome(false, $"Couldn't use the bucket: {ex.Message}");
        }
    }

    /// <summary>Replaces the database with a backup's, after taking a fresh backup of the current one. dotMARC then
    /// stops so it starts again on the restored data (Docker restarts it).</summary>
    public async Task<BackupOutcome> RestoreAsync(string key, AuditActor actor, CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new BackupOutcome(false, "A backup or restore is already running.");
        }

        var work = Directory.CreateTempSubdirectory("dotmarc-restore-");
        try
        {
            var (_, store, passphrase) = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var encrypted = Path.Combine(work.FullName, "payload.enc");
            await store.DownloadAsync(key, encrypted, cancellationToken).ConfigureAwait(false);

            var dump = Path.Combine(work.FullName, "dotmarc.dump");
            try
            {
                await ExtractDumpAsync(encrypted, passphrase, dump, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or InvalidDataException or EndOfStreamException)
            {
                return new BackupOutcome(false, "Couldn't decrypt that backup. Is the passphrase the one it was made with?");
            }

            var safety = await BackUpCoreAsync(suffix: "-before-restore", "before a restore", cancellationToken).ConfigureAwait(false);
            if (!safety.Succeeded)
            {
                return new BackupOutcome(false, $"Didn't restore: the backup of the current data, taken first in case, failed. {safety.Message}");
            }

            await dumper.RestoreAsync(dump, cancellationToken).ConfigureAwait(false);
            await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                AuditLog.Record(db, actor, AuditActions.BackupRestored, AuditTarget.Settings("Backups"),
                    $"Restored {Path.GetFileName(key)}; the data before it was backed up as {Path.GetFileName(safety.Key)}");
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogWarning("Restored backup {Key}. Stopping so dotMARC starts again on the restored data.", key);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                lifetime.StopApplication();
            }, CancellationToken.None);
            return new BackupOutcome(true, $"Restored. dotMARC is restarting on the restored data; reload the page in a minute. The data from before was backed up as {Path.GetFileName(safety.Key)}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Restoring backup {Key} failed.", key);
            return new BackupOutcome(false, $"Restore failed, and nothing was changed: {ex.Message}");
        }
        finally
        {
            work.Delete(recursive: true);
            _gate.Release();
        }
    }

    private async Task<BackupOutcome> BackUpCoreAsync(string suffix, string trigger, CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("dotmarc-backup-");
        var started = DateTimeOffset.UtcNow;
        var step = "reading the backup settings";
        string? destination = null, key = null;
        long? databaseBytes = null, encryptedBytes = null;
        var pruned = 0;
        IBackupStore? store = null;
        BackupSettings? settings = null;
        BackupOutcome outcome;
        try
        {
            (settings, store, var passphrase) = await OpenAsync(cancellationToken).ConfigureAwait(false);
            destination = $"{settings.Bucket}/{settings.Prefix}";
            var dump = Path.Combine(work.FullName, "dotmarc.dump");
            step = "dumping the database";
            await dumper.DumpAsync(dump, cancellationToken).ConfigureAwait(false);
            databaseBytes = new FileInfo(dump).Length;

            var payload = Path.Combine(work.FullName, "payload.enc");
            step = "compressing and encrypting";
            await WriteArchiveAsync(payload, dump, passphrase, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(payload).Length;
            encryptedBytes = size;
            key = $"{settings.Prefix}/{FilePrefix}{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}{suffix}.tar.gz.enc";
            step = "uploading to S3";
            await store.UploadAsync(key, payload, cancellationToken).ConfigureAwait(false);
            step = "removing old backups";
            pruned = await PruneAsync(store, settings, cancellationToken).ConfigureAwait(false);
            outcome = new BackupOutcome(true, $"Backed up {FormatSize(size)}.", key, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Offsite backup failed while {Step}.", step);
            outcome = new BackupOutcome(false, ex.Message);
        }
        finally
        {
            work.Delete(recursive: true);
        }

        // What's stored now, for the email. A listing that fails only leaves those figures out.
        IReadOnlyList<StoredBackup>? stored = null;
        if (outcome.Succeeded && store is not null && settings is not null)
        {
            try
            {
                stored = (await store.ListAsync(settings.Prefix + "/", cancellationToken).ConfigureAwait(false))
                    .Where(item => item.FileName.StartsWith(FilePrefix, StringComparison.Ordinal)).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Listing the stored backups for the backup email failed.");
            }
        }

        var before = await RecordOutcomeAsync(outcome, cancellationToken).ConfigureAwait(false);
        var finished = DateTimeOffset.UtcNow;
        var report = new BackupRunReport(outcome.Succeeded, trigger, started, finished, destination, key, databaseBytes, encryptedBytes, pruned,
            stored?.Count, stored?.Sum(item => item.Size), stored?.Count > 0 ? stored.Min(item => item.LastModifiedUtc) : null,
            outcome.Succeeded ? null : step, outcome.Succeeded ? null : outcome.Message, before.LastSuccessUtc,
            before.Enabled ? OffsiteBackupScheduler.NextRunUtc(before, finished) : null);
        if (BackupNotifier.ShouldSend(before, report) && await notifier.SendAsync(before, report, cancellationToken).ConfigureAwait(false) && !outcome.Succeeded)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            (await BackupSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false)).LastFailureEmailUtc = finished;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<(BackupSettings Settings, IBackupStore Store, string Passphrase)> OpenAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var settings = await BackupSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
        if (WhyNot(settings) is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        var secret = await secretStore.GetSecretAsync(BackupSettings.SecretAccessKeyStoreKey, cancellationToken).ConfigureAwait(false);
        var passphrase = await secretStore.GetSecretAsync(BackupSettings.PassphraseStoreKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(passphrase))
        {
            throw new InvalidOperationException("The secret access key or passphrase couldn't be read. Enter them again.");
        }

        return (settings, storeFactory.Create(settings, secret), passphrase);
    }

    /// <summary>backup/dotmarc.dump, backup/settings.env and backup/manifest.json, tarred, gzipped and encrypted.</summary>
    private async Task WriteArchiveAsync(string payloadPath, string dumpPath, string passphrase, CancellationToken cancellationToken)
    {
        var archive = payloadPath + ".tar.gz";
        await using (var gzip = new GZipStream(File.Create(archive), CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            await tar.WriteEntryAsync(dumpPath, "backup/dotmarc.dump", cancellationToken).ConfigureAwait(false);
            await WriteTextEntryAsync(tar, "backup/settings.env", SettingsEnv(), cancellationToken).ConfigureAwait(false);
            await WriteTextEntryAsync(tar, "backup/manifest.json", JsonSerializer.Serialize(new
            {
                createdUtc = DateTimeOffset.UtcNow,
                version = typeof(OffsiteBackupService).Assembly.GetName().Version?.ToString(3),
                host = Environment.MachineName,
            }), cancellationToken).ConfigureAwait(false);
        }

        await using var input = File.OpenRead(archive);
        await using var output = File.Create(payloadPath);
        await OpenSslEncryption.EncryptAsync(input, output, passphrase, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteTextEntryAsync(TarWriter tar, string name, string text, CancellationToken cancellationToken)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)) };
        await tar.WriteEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExtractDumpAsync(string encryptedPath, string passphrase, string dumpPath, CancellationToken cancellationToken)
    {
        var archive = dumpPath + ".tar.gz";
        await using (var input = File.OpenRead(encryptedPath))
        await using (var plain = File.Create(archive))
        {
            await OpenSslEncryption.DecryptAsync(input, plain, passphrase, cancellationToken).ConfigureAwait(false);
        }

        await using var gzip = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.Name == "backup/dotmarc.dump")
            {
                await entry.ExtractToFileAsync(dumpPath, overwrite: true, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        throw new InvalidDataException("The backup has no database in it.");
    }

    /// <summary>The settings dotMARC is running with, under the names the DigitalOcean deployment's .env uses, so a
    /// server can be rebuilt by hand from a backup if need be.</summary>
    private string SettingsEnv()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DotMarc"));
        var lines = new List<string> { "# dotMARC's settings when this backup was taken, in the DigitalOcean deployment's .env names.", $"POSTGRES_PASSWORD={connection.Password}" };
        foreach (var (name, key) in new[]
        {
            ("DOTMARC_HOSTNAME", "MtaSts:HostingHostname"), ("GRAPH_CLIENT_ID", "Graph:ClientId"), ("GRAPH_TENANT_ID", "Graph:TenantId"),
            ("GRAPH_CLIENT_SECRET", "Graph:ClientSecret"), ("GRAPH_MAILBOX_ADDRESS", "Graph:MailboxAddress"), ("GRAPH_TLSRPT_MAILBOX_ADDRESS", "Graph:TlsrptMailboxAddress"),
            ("ENTRAID_TENANT_ID", "EntraId:TenantId"), ("ENTRAID_CLIENT_ID", "EntraId:ClientId"), ("ENTRAID_CLIENT_SECRET", "EntraId:ClientSecret"),
            ("INITIAL_ADMIN_EMAILS", "InitialAdmins:Emails"),
        })
        {
            lines.Add($"{name}={configuration[key]}");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>Deletes daily backups older than the settings' days kept. One taken on the 1st of a month is kept.</summary>
    private static async Task<int> PruneAsync(IBackupStore store, BackupSettings settings, CancellationToken cancellationToken)
    {
        var pruned = 0;
        var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.RetentionDays);
        foreach (var item in await store.ListAsync(settings.Prefix + "/", cancellationToken).ConfigureAwait(false))
        {
            var stamp = item.FileName.StartsWith(FilePrefix, StringComparison.Ordinal) ? item.FileName[FilePrefix.Length..] : "";
            var isMonthly = stamp.Length >= 10 && stamp[8..10] == "01"; // yyyy-MM-dd
            if (stamp.Length > 0 && !isMonthly && item.LastModifiedUtc < cutoff)
            {
                await store.DeleteAsync(item.Key, cancellationToken).ConfigureAwait(false);
                pruned++;
            }
        }

        return pruned;
    }

    /// <summary>Saves the run's result, raising or resolving the BackupFailed alert. Returns the settings as they were
    /// before this run, for its email.</summary>
    private async Task<BackupSettings> RecordOutcomeAsync(BackupOutcome outcome, CancellationToken cancellationToken)
    {
        BackupSettings before;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            var settings = await BackupSettingsService.GetAsync(db, cancellationToken).ConfigureAwait(false);
            before = new BackupSettings
            {
                Enabled = settings.Enabled, RunHourUtc = settings.RunHourUtc, NotifyRecipients = [.. settings.NotifyRecipients],
                NotifyOnSuccess = settings.NotifyOnSuccess, LastFailureEmailUtc = settings.LastFailureEmailUtc,
                LastRunUtc = settings.LastRunUtc, LastRunSucceeded = settings.LastRunSucceeded, LastSuccessUtc = settings.LastSuccessUtc,
            };
            settings.LastRunUtc = DateTimeOffset.UtcNow;
            settings.LastRunSucceeded = outcome.Succeeded;
            settings.LastRunMessage = outcome.Message.Length > 1000 ? outcome.Message[..1000] : outcome.Message;
            if (outcome.Succeeded)
            {
                settings.LastSuccessUtc = settings.LastRunUtc;
                settings.LastBackupKey = outcome.Key;
                settings.LastBackupSize = outcome.Size;
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (outcome.Succeeded)
        {
            await alerting.ResolveBackupFailedAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await alerting.RaiseBackupFailedAsync(outcome.Message, cancellationToken).ConfigureAwait(false);
        }

        return before;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} bytes",
    };
}
