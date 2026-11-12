using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Senders;

[Collection("Postgres")]
public sealed class SourceSuggestionSettingsServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SourceSuggestionSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task SuggestionsStartOff()
    {
        await using var context = CreateContext();

        var settings = await SourceSuggestionSettingsService.GetAsync(context);

        Assert.False(settings.Enabled);
        Assert.False(settings.IsReady());
    }

    [Fact]
    public async Task Saving_StoresTheKeyAsASecret_AndAuditsWithoutIt()
    {
        await using var context = CreateContext();
        var secrets = new FakeSecretStore();
        var updated = await SourceSuggestionSettingsService.GetAsync(context);
        updated.Enabled = true;
        updated.Endpoint = "https://models.example/v1/";
        updated.Model = "test-model";

        await SourceSuggestionSettingsService.SaveAsync(context, TestActors.Admin, secrets, updated, "the-api-key");

        await using var verify = CreateContext();
        var saved = await SourceSuggestionSettingsService.GetAsync(verify);
        Assert.True(saved.IsReady());
        Assert.Equal("https://models.example/v1", saved.Endpoint);
        Assert.Equal("the-api-key", secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey]);
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Equal(AuditActions.SourceSuggestionSettingsSaved, entry.Action);
        Assert.DoesNotContain("the-api-key", entry.Summary);
        Assert.DoesNotContain(entry.Changes, change => change.New == "the-api-key");
    }

    [Fact]
    public async Task TurningOn_WithoutAKey_IsRefused()
    {
        await using var context = CreateContext();
        var updated = await SourceSuggestionSettingsService.GetAsync(context);
        updated.Enabled = true;
        updated.Endpoint = "https://models.example/v1";
        updated.Model = "test-model";

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            SourceSuggestionSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));

        Assert.Contains("API key", exception.Message);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("https://user:secret@models.example/v1")]
    [InlineData("ftp://models.example/v1")]
    public async Task ABadEndpoint_IsRefused(string endpoint)
    {
        await using var context = CreateContext();
        var updated = await SourceSuggestionSettingsService.GetAsync(context);
        updated.Enabled = true;
        updated.Endpoint = endpoint;
        updated.Model = "test-model";

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SourceSuggestionSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, "key"));
    }
}
