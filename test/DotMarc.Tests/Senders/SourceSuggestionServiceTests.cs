using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Senders;

[Collection("Postgres")]
public sealed class SourceSuggestionServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SourceSuggestionServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static readonly SourceSuggestionFacts Facts = new(
        "203.0.113.9", null, null, null, 2, "Fail", "Fail", "none", null, false, true, "Likely spoofing.", null, null, []);

    [Fact]
    public async Task ASuggestion_DoesNotChangeTheDecision_AndIsAudited()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        var settings = await context.SourceSuggestionSettings.SingleAsync();
        settings.Enabled = true;
        settings.Endpoint = "https://models.example/v1";
        settings.Model = "test-model";
        settings.ApiKeyConfigured = true;
        await context.SaveChangesAsync();

        var secrets = new FakeSecretStore();
        secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey] = "the-api-key";
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.LikelySpoof, "Both checks failed."));

        var result = await SourceSuggestionService.SuggestAsync(context, secrets, client, TestActors.Admin, domain, Facts);

        Assert.Equal(SourceSuggestionVerdict.LikelySpoof, result.Verdict);
        Assert.Equal(Facts, client.Facts);
        Assert.Empty(await context.SenderApprovals.ToListAsync());
        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.SourceSuggestionRequested);
        Assert.Contains("Likely spoof", entry.Summary);
        Assert.Contains("contoso.com", entry.Summary);
        Assert.DoesNotContain("the-api-key", entry.Summary);
    }

    [Fact]
    public async Task AnUnconfiguredProvider_DoesNotCallTheModel_OrWriteAnAuditEntry()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Unknown, "no"));

        var result = await SourceSuggestionService.SuggestAsync(context, new FakeSecretStore(), client, TestActors.Admin, domain, Facts);

        Assert.NotNull(result.Error);
        Assert.Null(client.Facts);
        Assert.Empty(await context.AuditEntries.Where(audit => audit.Action == AuditActions.SourceSuggestionRequested).ToListAsync());
        Assert.Empty(await context.SenderApprovals.ToListAsync());
    }

    [Fact]
    public async Task AProviderError_IsAudited_AndDoesNotDecide()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        var settings = await context.SourceSuggestionSettings.SingleAsync();
        settings.Enabled = true;
        settings.Endpoint = "https://models.example/v1";
        settings.Model = "test-model";
        settings.ApiKeyConfigured = true;
        await context.SaveChangesAsync();
        var secrets = new FakeSecretStore();
        secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey] = "the-api-key";
        var client = new StubClient(SourceSuggestionResult.Failed("The suggestion timed out. Nothing was changed."));

        var result = await SourceSuggestionService.SuggestAsync(context, secrets, client, TestActors.Admin, domain, Facts);

        Assert.Contains("timed out", result.Error);
        Assert.Empty(await context.SenderApprovals.ToListAsync());
        var entry = await context.AuditEntries.SingleAsync();
        Assert.Contains("didn't answer", entry.Summary);
        Assert.DoesNotContain("the-api-key", entry.Summary);
    }

    private sealed class StubClient(SourceSuggestionResult result) : ISourceSuggestionClient
    {
        public SourceSuggestionFacts? Facts { get; private set; }

        public Task<SourceSuggestionResult> SuggestAsync(string endpoint, string model, string apiKey, SourceSuggestionFacts facts, CancellationToken cancellationToken)
        {
            Facts = facts;
            return Task.FromResult(result);
        }
    }
}
