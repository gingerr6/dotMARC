namespace DotMarc.Backups;

/// <summary>Singleton settings row for offsite backups, with the last run's outcome. The S3 secret key and the
/// encryption passphrase live in ISecretStore, never on this entity.</summary>
public sealed class BackupSettings
{
    public const string SecretAccessKeyStoreKey = "Backups.SecretAccessKey";
    public const string PassphraseStoreKey = "Backups.Passphrase";

    public int Id { get; set; }
    public bool Enabled { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string Prefix { get; set; } = "dotmarc-backups";

    /// <summary>For an S3-compatible service other than AWS, such as DigitalOcean Spaces. Null for AWS.</summary>
    public string? EndpointUrl { get; set; }

    public string? AccessKeyId { get; set; }
    public bool SecretAccessKeyConfigured { get; set; }
    public bool PassphraseConfigured { get; set; }

    /// <summary>The hour, UTC, the nightly backup runs.</summary>
    public int RunHourUtc { get; set; } = 3;

    /// <summary>How long daily backups are kept. A backup taken on the 1st of a month is kept until deleted by hand.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Who's emailed after each backup: always when it fails, and when it works too if NotifyOnSuccess.</summary>
    public List<string> NotifyRecipients { get; set; } = [];
    public bool NotifyOnSuccess { get; set; } = true;

    /// <summary>When a failure was last emailed, so a backup that keeps failing isn't emailed every hourly retry.</summary>
    public DateTimeOffset? LastFailureEmailUtc { get; set; }

    public DateTimeOffset? LastRunUtc { get; set; }
    public bool? LastRunSucceeded { get; set; }
    public string? LastRunMessage { get; set; }
    public DateTimeOffset? LastSuccessUtc { get; set; }
    public string? LastBackupKey { get; set; }
    public long? LastBackupSize { get; set; }

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Bucket) && !string.IsNullOrWhiteSpace(Region) && !string.IsNullOrWhiteSpace(AccessKeyId)
        && SecretAccessKeyConfigured && PassphraseConfigured;
}
