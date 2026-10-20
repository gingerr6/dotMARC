using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using DotMarc.Audit;
using DotMarc.Backups;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Backups;

[Collection("Postgres")]
public sealed class OffsiteBackupServiceTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;
    private readonly FakeSecretStore _secrets = new();
    private readonly FakeBackupStore _store = new();
    private readonly FakeDumper _dumper = new();
    private readonly RecordingAlerts _alerts = new();
    private readonly FakeLifetime _lifetime = new();

    public OffsiteBackupServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        _store.Dispose();
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private OffsiteBackupService CreateService()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DotMarc"] = "Host=postgres;Database=dotmarc;Username=dotmarc;Password=s3cret",
            ["Graph:ClientId"] = "graph-client",
            ["MtaSts:HostingHostname"] = "dmarc.example.com",
        }).Build();
        return new OffsiteBackupService(new FakeDbContextFactory(_connectionString), _secrets, new FakeStoreFactory(_store), _dumper, _alerts,
            configuration, _lifetime, NullLogger<OffsiteBackupService>.Instance);
    }

    private async Task ConfigureAsync()
    {
        await using var context = CreateContext();
        await BackupSettingsService.SaveAsync(context, TestActors.Admin, _secrets,
            new BackupSettings { Enabled = true, Bucket = "bucket", Region = "eu-west-2", Prefix = "/dotmarc-backups/", AccessKeyId = "AKIA1" },
            newSecretAccessKey: "secret", newPassphrase: Passphrase);
    }

    [Fact]
    public async Task WhyNot_AsksForTheSettings_UntilTheyAreComplete()
    {
        await using var context = CreateContext();
        var settings = await BackupSettingsService.GetAsync(context);

        Assert.NotNull(CreateService().WhyNot(settings));
        Assert.False((await CreateService().BackUpAsync()).Succeeded);
    }

    [Fact]
    public async Task BackUp_UploadsTheEncryptedDumpAndSettings_AndRecordsIt()
    {
        await ConfigureAsync();

        var outcome = await CreateService().BackUpAsync();

        Assert.True(outcome.Succeeded, outcome.Message);
        var key = Assert.Single(_store.Objects.Keys);
        Assert.Equal(outcome.Key, key);
        Assert.StartsWith("dotmarc-backups/dotmarc-backup-", key);
        Assert.EndsWith(".tar.gz.enc", key);

        var entries = await ReadArchiveAsync(_store.Objects[key], Passphrase);
        Assert.Equal(_dumper.Content, entries["backup/dotmarc.dump"]);
        var env = Encoding.UTF8.GetString(entries["backup/settings.env"]);
        Assert.Contains("POSTGRES_PASSWORD=s3cret\n", env);
        Assert.Contains("GRAPH_CLIENT_ID=graph-client\n", env);
        Assert.Contains("DOTMARC_HOSTNAME=dmarc.example.com\n", env);
        Assert.True(entries.ContainsKey("backup/manifest.json"));

        await using var context = CreateContext();
        var settings = await BackupSettingsService.GetAsync(context);
        Assert.True(settings.LastRunSucceeded);
        Assert.Equal(key, settings.LastBackupKey);
        Assert.NotNull(settings.LastSuccessUtc);
        Assert.Equal(1, _alerts.BackupResolves);
    }

    [Fact]
    public async Task AFailedBackup_IsRecorded_AndRaisesTheAlert()
    {
        await ConfigureAsync();
        _dumper.Fail = true;

        var outcome = await CreateService().BackUpAsync();

        Assert.False(outcome.Succeeded);
        Assert.Empty(_store.Objects);
        Assert.Single(_alerts.BackupFailures);
        await using var context = CreateContext();
        var settings = await BackupSettingsService.GetAsync(context);
        Assert.False(settings.LastRunSucceeded);
        Assert.Null(settings.LastSuccessUtc);
    }

    [Fact]
    public async Task BackUp_DeletesOldDailies_ButKeepsMonthlies()
    {
        await ConfigureAsync();
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        _store.Add("dotmarc-backups/dotmarc-backup-2026-09-10_031500.tar.gz.enc", old);
        _store.Add("dotmarc-backups/dotmarc-backup-2026-09-01_031500.tar.gz.enc", old);
        _store.Add("dotmarc-backups/dotmarc-backup-2026-10-19_031500.tar.gz.enc", DateTimeOffset.UtcNow.AddDays(-1));
        _store.Add("dotmarc-backups/something-else.txt", old);

        await CreateService().BackUpAsync();

        Assert.DoesNotContain("dotmarc-backups/dotmarc-backup-2026-09-10_031500.tar.gz.enc", _store.Objects.Keys);
        Assert.Contains("dotmarc-backups/dotmarc-backup-2026-09-01_031500.tar.gz.enc", _store.Objects.Keys);
        Assert.Contains("dotmarc-backups/dotmarc-backup-2026-10-19_031500.tar.gz.enc", _store.Objects.Keys);
        Assert.Contains("dotmarc-backups/something-else.txt", _store.Objects.Keys);
    }

    [Fact]
    public async Task Restore_BacksUpTheCurrentDataFirst_RestoresTheDump_AndRestarts()
    {
        await ConfigureAsync();
        var service = CreateService();
        var original = _dumper.Content;
        var key = (await service.BackUpAsync()).Key!;
        _dumper.Content = Encoding.UTF8.GetBytes("newer data");

        var outcome = await service.RestoreAsync(key, TestActors.Admin);

        Assert.True(outcome.Succeeded, outcome.Message);
        Assert.Equal(original, _dumper.Restored);
        Assert.Contains(_store.Objects.Keys, stored => stored.EndsWith("-before-restore.tar.gz.enc", StringComparison.Ordinal));
        await using (var context = CreateContext())
        {
            Assert.Contains(context.AuditEntries, entry => entry.Action == AuditActions.BackupRestored);
        }

        for (var waited = 0; waited < 50 && !_lifetime.Stopped; waited++)
        {
            await Task.Delay(100);
        }

        Assert.True(_lifetime.Stopped);
    }

    [Fact]
    public async Task Restore_WithTheWrongPassphrase_ChangesNothing()
    {
        await ConfigureAsync();
        var service = CreateService();
        var key = (await service.BackUpAsync()).Key!;
        _secrets.Secrets[BackupSettings.PassphraseStoreKey] = "a different passphrase";

        var outcome = await service.RestoreAsync(key, TestActors.Admin);

        Assert.False(outcome.Succeeded);
        Assert.Contains("passphrase", outcome.Message);
        Assert.Null(_dumper.Restored);
        Assert.Single(_store.Objects); // no safety backup either
    }

    private static async Task<Dictionary<string, byte[]>> ReadArchiveAsync(byte[] encrypted, string passphrase)
    {
        using var archive = new MemoryStream();
        await OpenSslEncryption.DecryptAsync(new MemoryStream(encrypted), archive, passphrase);
        archive.Position = 0;
        var entries = new Dictionary<string, byte[]>();
        await using var tar = new TarReader(new GZipStream(archive, CompressionMode.Decompress));
        while (await tar.GetNextEntryAsync() is { } entry)
        {
            using var content = new MemoryStream();
            await entry.DataStream!.CopyToAsync(content);
            entries[entry.Name] = content.ToArray();
        }

        return entries;
    }

    private sealed class FakeDumper : IDatabaseDumper
    {
        public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("PGDMP pretend dump");
        public byte[]? Restored { get; private set; }
        public bool Fail { get; set; }
        public string? Unavailable => null;

        public Task DumpAsync(string filePath, CancellationToken cancellationToken) =>
            Fail ? throw new InvalidOperationException("pg_dump failed: no route to host") : File.WriteAllBytesAsync(filePath, Content, cancellationToken);

        public async Task RestoreAsync(string filePath, CancellationToken cancellationToken) =>
            Restored = await File.ReadAllBytesAsync(filePath, cancellationToken);
    }

    private sealed class FakeStoreFactory(FakeBackupStore store) : IBackupStoreFactory
    {
        public IBackupStore Create(BackupSettings settings, string secretAccessKey) => store;
    }

    private sealed class FakeBackupStore : IBackupStore, IDisposable
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        private readonly Dictionary<string, DateTimeOffset> _modified = [];

        public void Add(string key, DateTimeOffset modified)
        {
            Objects[key] = [1];
            _modified[key] = modified;
        }

        public async Task UploadAsync(string key, string filePath, CancellationToken cancellationToken)
        {
            Objects[key] = await File.ReadAllBytesAsync(filePath, cancellationToken);
            _modified[key] = DateTimeOffset.UtcNow;
        }

        public Task<IReadOnlyList<StoredBackup>> ListAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredBackup>>(Objects.Where(item => item.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(item => new StoredBackup(item.Key, _modified[item.Key], item.Value.Length)).ToList());

        public Task DownloadAsync(string key, string filePath, CancellationToken cancellationToken) =>
            File.WriteAllBytesAsync(filePath, Objects[key], cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool Stopped { get; private set; }
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped = true;
    }
}
