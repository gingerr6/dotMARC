namespace DotMarc.Backups;

public sealed record StoredBackup(string Key, DateTimeOffset LastModifiedUtc, long Size)
{
    public string FileName => Key[(Key.LastIndexOf('/') + 1)..];
}

/// <summary>Where backups are kept: an S3 bucket (S3BackupStore), or a fake in tests.</summary>
public interface IBackupStore
{
    Task UploadAsync(string key, string filePath, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredBackup>> ListAsync(string prefix, CancellationToken cancellationToken);
    Task DownloadAsync(string key, string filePath, CancellationToken cancellationToken);
    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

/// <summary>Opens the store described by settings, with their secret key.</summary>
public interface IBackupStoreFactory
{
    IBackupStore Create(BackupSettings settings, string secretAccessKey);
}
