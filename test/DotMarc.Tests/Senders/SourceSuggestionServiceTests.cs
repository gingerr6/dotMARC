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
        "contoso.com", "203.0.113.9", null, null, null, 2, "Fail", "Fail", "none", null, false, true, "Likely spoofing.", null, null, ["spf bounce.example.net: pass"]);

    private static async Task AllowAsync(DotMarcDbContext context, AuditActor actor, bool portal = false)
    {
        var role = new Role
        {
            Name = "May suggest " + Guid.NewGuid().ToString("N"),
            IsScopable = portal,
            Permissions = [Permission.DomainsView, Permission.SourceSuggestions],
        };
        var groups = portal ? new List<Group> { new() { Name = "Client " + Guid.NewGuid().ToString("N") } } : [];
        context.UserAccesses.Add(new UserAccess
        {
            Email = actor.Email!,
            EntraObjectId = actor.ObjectId,
            Role = role,
            ScopedGroups = groups,
            IsClientPortal = portal,
        });
        await context.SaveChangesAsync();
    }

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
        await AllowAsync(context, TestActors.Admin);
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
        await AllowAsync(context, TestActors.Admin);
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
        await AllowAsync(context, TestActors.Admin);
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

    [Fact]
    public async Task SomeoneWithoutThePermission_IsRefused_AndTheModelIsNotCalled()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await ConfigureProviderAsync(context);
        var role = new Role { Name = "Viewer only", Permissions = [Permission.DomainsView] };
        context.UserAccesses.Add(new UserAccess { Email = TestActors.Admin.Email!, EntraObjectId = TestActors.Admin.ObjectId, Role = role });
        await context.SaveChangesAsync();
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Unknown, "no"));

        var result = await SourceSuggestionService.SuggestAsync(context, new FakeSecretStore(), client, TestActors.Admin, domain, Facts);

        Assert.Equal(SourceSuggestionAccess.RefusedMessage, result.Error);
        Assert.Null(client.Facts);
        Assert.Empty(await context.AuditEntries.Where(audit => audit.Action == AuditActions.SourceSuggestionRequested).ToListAsync());
    }

    [Fact]
    public async Task APermittedPerson_CanAsk_WhenTheirOwnSwitchIsOff()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await ConfigureProviderAsync(context);
        await AllowAsync(context, TestActors.Admin);
        await context.SaveChangesAsync();
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(context, TestActors.Admin));
        var secrets = new FakeSecretStore();
        secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey] = "the-api-key";
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Forwarder, "Passed on."));

        var result = await SourceSuggestionService.SuggestAsync(context, secrets, client, TestActors.Admin, domain, Facts);

        Assert.Equal(SourceSuggestionVerdict.Forwarder, result.Verdict);
        Assert.NotNull(client.Facts);
    }

    [Fact]
    public async Task AnApiKey_IsRefused_EvenWhenItsRoleIncludesThePermission()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await ConfigureProviderAsync(context);
        var role = new Role { Name = "Key role", Permissions = [Permission.DomainsView, Permission.SourceSuggestions] };
        context.Roles.Add(role);
        await context.SaveChangesAsync();
        var created = await ApiKeyManagementService.CreateAsync(context, TestActors.Admin, "Suggestions key", role.Id, [], 90);
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Unknown, "no"));

        var result = await SourceSuggestionService.SuggestAsync(context, new FakeSecretStore(), client,
            AuditActor.ForApiKey(created.Key!.Id, created.Key.Name, "Test Admin"), domain, Facts);

        Assert.Equal(SourceSuggestionAccess.RefusedMessage, result.Error);
        Assert.Null(client.Facts);
        Assert.Empty(await context.AuditEntries.Where(audit => audit.Action == AuditActions.SourceSuggestionRequested).ToListAsync());
    }

    [Fact]
    public async Task AClientPortalUser_IsRefused_EvenWhenTheirRoleIncludesThePermission()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await ConfigureProviderAsync(context);
        await AllowAsync(context, TestActors.Admin, portal: true);
        await context.SaveChangesAsync();
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Unknown, "no"));

        var result = await SourceSuggestionService.SuggestAsync(context, new FakeSecretStore(), client, TestActors.Admin, domain, Facts);

        Assert.Equal(SourceSuggestionAccess.RefusedMessage, result.Error);
        Assert.Null(client.Facts);
    }

    [Fact]
    public async Task APersonLimitedToGroups_MayAsk_WhenTheirRoleIncludesThePermission()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await ConfigureProviderAsync(context);
        var role = new Role
        {
            Name = "Client reviewer",
            IsScopable = true,
            Permissions = [Permission.DomainsView, Permission.SourceSuggestions],
        };
        context.UserAccesses.Add(new UserAccess
        {
            Email = TestActors.Admin.Email!,
            EntraObjectId = TestActors.Admin.ObjectId,
            Role = role,
            ScopedGroups = [new Group { Name = "Aurora" }],
        });
        await context.SaveChangesAsync();
        var secrets = new FakeSecretStore();
        secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey] = "the-api-key";
        var client = new StubClient(SourceSuggestionResult.Ok(SourceSuggestionVerdict.Unknown, "Can't tell."));

        var result = await SourceSuggestionService.SuggestAsync(context, secrets, client, TestActors.Admin, domain, Facts);

        Assert.Equal(SourceSuggestionVerdict.Unknown, result.Verdict);
    }

    [Fact]
    public void Viewer_DoesNotIncludeSourceSuggestions_ByDefault() =>
        Assert.DoesNotContain(Permission.SourceSuggestions, AccessBootstrapper.ViewerPermissions);

    private static async Task ConfigureProviderAsync(DotMarcDbContext context)
    {
        var settings = await context.SourceSuggestionSettings.SingleAsync();
        settings.Enabled = true;
        settings.Endpoint = "https://models.example/v1";
        settings.Model = "test-model";
        settings.ApiKeyConfigured = true;
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
