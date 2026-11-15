using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Notifications;

[Collection("Postgres")]
public sealed class AlertFixSuggestionServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public AlertFixSuggestionServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task ASuggestion_ChangesNothing_AndIsAudited()
    {
        await using var context = CreateContext();
        var (alert, domain) = await SeedAsync(context);
        await ConfigureProviderAsync(context);
        await AllowAsync(context, portal: false);
        var secrets = Secrets();
        var client = new StubClient(AlertFixResult.Ok("Merge the two SPF records.", ["Merge them.", "Delete the second."]));

        var result = await AlertFixSuggestionService.SuggestAsync(context, secrets, client, TestActors.Admin, alert, domain);

        Assert.Null(result.Error);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("contoso.com", client.Facts!.Domain);
        Assert.Contains(client.Facts.DnsChecks, check => check.StartsWith("SPF", StringComparison.Ordinal));

        await using var read = CreateContext();
        var stored = await read.AlertEvents.SingleAsync();
        Assert.False(stored.IsResolved);
        var entry = await read.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.AlertFixSuggestionRequested);
        Assert.Contains("SPF record broken", entry.Summary);
        Assert.Contains("contoso.com", entry.Summary);
        Assert.Contains("Merge the two SPF records.", entry.Summary);
        Assert.DoesNotContain("the-api-key", entry.Summary);
    }

    [Fact]
    public async Task AProviderError_IsAudited()
    {
        await using var context = CreateContext();
        var (alert, domain) = await SeedAsync(context);
        await ConfigureProviderAsync(context);
        await AllowAsync(context, portal: false);
        var client = new StubClient(AlertFixResult.Failed("The suggestion timed out. Nothing was changed."));

        var result = await AlertFixSuggestionService.SuggestAsync(context, Secrets(), client, TestActors.Admin, alert, domain);

        Assert.Contains("timed out", result.Error);
        var entry = await context.AuditEntries.SingleAsync(audit => audit.Action == AuditActions.AlertFixSuggestionRequested);
        Assert.Contains("didn't answer", entry.Summary);
    }

    [Fact]
    public async Task AnUnconfiguredProvider_DoesNotCallTheModel()
    {
        await using var context = CreateContext();
        var (alert, domain) = await SeedAsync(context);
        await AllowAsync(context, portal: false);
        var client = new StubClient(AlertFixResult.Ok("no", []));

        var result = await AlertFixSuggestionService.SuggestAsync(context, Secrets(), client, TestActors.Admin, alert, domain);

        Assert.NotNull(result.Error);
        Assert.Null(client.Facts);
        Assert.Empty(await context.AuditEntries.Where(audit => audit.Action == AuditActions.AlertFixSuggestionRequested).ToListAsync());
    }

    [Fact]
    public async Task AClientPortalUser_IsRefused()
    {
        await using var context = CreateContext();
        var (alert, domain) = await SeedAsync(context);
        await ConfigureProviderAsync(context);
        await AllowAsync(context, portal: true);
        var client = new StubClient(AlertFixResult.Ok("no", []));

        var result = await AlertFixSuggestionService.SuggestAsync(context, Secrets(), client, TestActors.Admin, alert, domain);

        Assert.Equal(SourceSuggestionAccess.RefusedMessage, result.Error);
        Assert.Null(client.Facts);
    }

    [Fact]
    public async Task SomeoneWithNoGrant_IsRefused()
    {
        await using var context = CreateContext();
        var (alert, domain) = await SeedAsync(context);
        await ConfigureProviderAsync(context);
        await context.SaveChangesAsync();
        var client = new StubClient(AlertFixResult.Ok("no", []));

        var result = await AlertFixSuggestionService.SuggestAsync(context, Secrets(), client, TestActors.Admin, alert, domain);

        Assert.Equal(SourceSuggestionAccess.RefusedMessage, result.Error);
        Assert.Null(client.Facts);
    }

    private static async Task<(AlertEvent Alert, Domain Domain)> SeedAsync(DotMarcDbContext context)
    {
        var domain = new Domain
        {
            Name = "contoso.com",
            IsMonitored = true,
            FirstSeenUtc = DateTimeOffset.UtcNow,
            SpfCheckStatus = SpfCheckStatus.MultipleRecords,
            SpfCheckedUtc = DateTimeOffset.UtcNow,
            SpfCheckDetail = "Two SPF records found.",
        };
        var alert = new AlertEvent
        {
            DomainName = "contoso.com",
            AlertType = AlertTypes.SpfRecordBroken,
            Severity = "Warning",
            Title = "SPF record broken on contoso.com",
            Message = "contoso.com publishes two SPF records.",
        };
        context.Domains.Add(domain);
        context.AlertEvents.Add(alert);
        await context.SaveChangesAsync();
        return (alert, domain);
    }

    private static FakeSecretStore Secrets()
    {
        var secrets = new FakeSecretStore();
        secrets.Secrets[SourceSuggestionSettings.ApiKeySecretKey] = "the-api-key";
        return secrets;
    }

    private static async Task ConfigureProviderAsync(DotMarcDbContext context)
    {
        var settings = await context.SourceSuggestionSettings.SingleAsync();
        settings.Enabled = true;
        settings.Endpoint = "https://models.example/v1";
        settings.Model = "test-model";
        settings.ApiKeyConfigured = true;
        await context.SaveChangesAsync();
    }

    private static async Task AllowAsync(DotMarcDbContext context, bool portal)
    {
        var role = new Role
        {
            Name = "Alerts " + Guid.NewGuid().ToString("N"),
            IsScopable = portal,
            Permissions = [Permission.AlertsView],
        };
        context.UserAccesses.Add(new UserAccess
        {
            Email = TestActors.Admin.Email!,
            EntraObjectId = TestActors.Admin.ObjectId,
            Role = role,
            ScopedGroups = portal ? [new Group { Name = "Client " + Guid.NewGuid().ToString("N") }] : [],
            IsClientPortal = portal,
        });
        await context.SaveChangesAsync();
    }

    private sealed class StubClient(AlertFixResult result) : IAlertFixClient
    {
        public AlertFixFacts? Facts { get; private set; }

        public Task<AlertFixResult> SuggestFixAsync(string endpoint, string model, string apiKey, AlertFixFacts facts, CancellationToken cancellationToken)
        {
            Facts = facts;
            return Task.FromResult(result);
        }
    }
}
