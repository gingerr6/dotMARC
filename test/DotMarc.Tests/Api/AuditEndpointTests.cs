using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class AuditEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public AuditEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<long> SeedEntryAsync(string action, string? targetType = null, string? targetId = null, string? targetName = null,
        AuditEntryKind kind = AuditEntryKind.Change, string summary = "something happened", DateTimeOffset? occurredUtc = null,
        AuditActorKind actorKind = AuditActorKind.User, string actorName = "Alex Admin", List<AuditFieldChange>? changes = null)
    {
        await using var context = _host.CreateContext();
        var entry = new AuditEntry
        {
            OccurredUtc = occurredUtc ?? DateTimeOffset.UtcNow, Kind = kind, ActorKind = actorKind, ActorName = actorName,
            ActorEmail = actorKind == AuditActorKind.User ? "alex@contoso.example" : null, Action = action,
            TargetType = targetType, TargetId = targetId, TargetName = targetName, Summary = summary, Changes = changes ?? [],
        };
        context.AuditEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry.Id;
    }

    private static string IdText(int id) => id.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheAuditLog_NeedsAuditView()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Entries_ComeNewestFirst_WithPageViewsLeftOutUnlessAskedFor()
    {
        var older = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", "990001", "api-audit-order.example", occurredUtc: DateTimeOffset.UtcNow.AddMinutes(-5), summary: "api-audit-order older");
        var newer = await SeedEntryAsync(AuditActions.DomainRemoved, "Domain", "990001", "api-audit-order.example", summary: "api-audit-order newer");
        var pageView = await SeedEntryAsync(AuditActions.PageViewed, kind: AuditEntryKind.PageView, summary: "api-audit-order page view");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-order");
        var withViews = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-order&kind=all,change");

        Assert.Equal([newer, older], page!.Items.Select(entry => entry.Id));
        Assert.Equal("change", page.Items[0].Kind);
        Assert.Contains(pageView, withViews!.Items.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Filters_NarrowByActionActorTargetAndDates()
    {
        var wanted = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", "990002", "api-audit-filter.example",
            actorKind: AuditActorKind.ApiKey, actorName: "api-audit-sync key", occurredUtc: new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));
        await SeedEntryAsync(AuditActions.DomainRemoved, "Domain", "990002", "api-audit-filter.example",
            occurredUtc: new DateTimeOffset(2026, 2, 11, 0, 0, 0, TimeSpan.Zero));
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>(
            $"/api/v1/audit?action={AuditActions.DomainAdded}&actorKind=APIKEY&actor=audit-sync&targetType=domain&targetId=990002&occurredAfter=2026-02-01&occurredBefore=2026-03-01");

        var entry = Assert.Single(page!.Items);
        Assert.Equal(wanted, entry.Id);
        Assert.Equal(("apiKey", "api-audit-sync key"), (entry.Actor.Kind, entry.Actor.Name));
        Assert.Equal(new ApiAuditTarget("Domain", "990002", "api-audit-filter.example"), entry.Target);
    }

    [Fact]
    public async Task SecretChanges_ComeBackWithoutValues()
    {
        await SeedEntryAsync(AuditActions.DomainAdded, summary: "api-audit-secret", changes: [new AuditFieldChange("Client secret", null, null, Secret: true), new AuditFieldChange("Name", "a", "b")]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var entry = Assert.Single((await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-secret"))!.Items);

        Assert.Equal([new ApiAuditChange("Client secret", null, null, true), new ApiAuditChange("Name", "a", "b", false)], entry.Changes);
    }

    [Fact]
    public async Task AGroupLimitedKey_SeesOnlyItsDomainsGroupsAndAlerts()
    {
        var ownGroup = await _host.SeedGroupAsync("api-audit-own");
        var otherGroup = await _host.SeedGroupAsync("api-audit-other");
        var ownDomain = await _host.SeedDomainAsync("api-audit-own.example", groupIds: [ownGroup]);
        var otherDomain = await _host.SeedDomainAsync("api-audit-other.example", groupIds: [otherGroup]);
        var byId = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", IdText(ownDomain), "api-audit-own.example", summary: "api-audit-scope 1");
        var byName = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", null, "api-audit-own.example", summary: "api-audit-scope 2");
        var group = await SeedEntryAsync(AuditActions.DomainAdded, "Group", IdText(ownGroup), "api-audit-own", summary: "api-audit-scope 3");
        var alert = await SeedEntryAsync(AuditActions.DomainAdded, "Alert", "990003", "api-audit-own.example", summary: "api-audit-scope 4");
        var other = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", IdText(otherDomain), "api-audit-other.example", summary: "api-audit-scope 5");
        var signIn = await SeedEntryAsync(AuditActions.SignInSucceeded, kind: AuditEntryKind.SignIn, summary: "api-audit-scope 6");
        var settings = await SeedEntryAsync(AuditActions.DomainAdded, "Settings", null, "Branding", summary: "api-audit-scope 7");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView], scopedGroupIds: [ownGroup]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-scope&pageSize=200");
        var forDomain = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?domain=api-audit-own.example&pageSize=200");
        var unseen = await client.GetAsync("/api/v1/audit?domain=api-audit-other.example");

        Assert.Equal(new[] { byId, byName, group, alert }.Order(), page!.Items.Select(entry => entry.Id).Order());
        Assert.DoesNotContain(other, page.Items.Select(entry => entry.Id));
        Assert.DoesNotContain(signIn, page.Items.Select(entry => entry.Id));
        Assert.DoesNotContain(settings, page.Items.Select(entry => entry.Id));
        Assert.Equal(new[] { byId, byName, alert }.Order(), forDomain!.Items.Select(entry => entry.Id).Order());
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }

    [Fact]
    public async Task AnUnknownAction_IsA400()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/audit?action=not.an.action");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuditActions.DomainAdded, await response.Content.ReadAsStringAsync());
    }
}
