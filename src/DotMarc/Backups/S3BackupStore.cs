using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace DotMarc.Backups;

public sealed class S3BackupStoreFactory : IBackupStoreFactory
{
    public IBackupStore Create(BackupSettings settings, string secretAccessKey) => new S3BackupStore(settings, secretAccessKey);
}

/// <summary>Backups in an S3 bucket, or an S3-compatible service when the settings give an endpoint.</summary>
public sealed class S3BackupStore : IBackupStore
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3BackupStore(BackupSettings settings, string secretAccessKey)
    {
        var config = new AmazonS3Config();
        if (settings.EndpointUrl is { } endpoint)
        {
            config.ServiceURL = endpoint;
            config.AuthenticationRegion = settings.Region;
            config.ForcePathStyle = true;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        _client = new AmazonS3Client(new BasicAWSCredentials(settings.AccessKeyId, secretAccessKey), config);
        _bucket = settings.Bucket!;
    }

    public async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken) =>
        await _client.PutObjectAsync(new PutObjectRequest { BucketName = _bucket, Key = key, FilePath = filePath }, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<StoredBackup>> ListAsync(string prefix, CancellationToken cancellationToken)
    {
        var found = new List<StoredBackup>();
        var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix };
        ListObjectsV2Response response;
        do
        {
            response = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                found.Add(new StoredBackup(item.Key, new DateTimeOffset(item.LastModified ?? DateTime.UtcNow, TimeSpan.Zero).ToUniversalTime(), item.Size ?? 0));
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return found;
    }

    public async Task DownloadAsync(string key, string filePath, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(filePath);
        await response.ResponseStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
}
