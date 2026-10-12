using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class GroupAndTagEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public GroupAndTagEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Groups_ListsEveryGroupWithItsDomainCount()
    {
        var groupId = await _host.SeedGroupAsync("api-groups-a");
        await _host.SeedDomainAsync("api-groups-a1.example", groupIds: [groupId]);
        await _host.SeedDomainAsync("api-groups-a2.example", groupIds: [groupId]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups");

        Assert.Contains(new ApiGroup(groupId, "api-groups-a", 2), groups!);
    }

    [Fact]
    public async Task AScopedKey_SeesOnlyItsGroups()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-groups-own");
        await _host.SeedGroupAsync("api-groups-other");
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView], scopedGroupIds: [ownGroupId]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups");

        Assert.Equal([ownGroupId], groups!.Select(group => group.Id));
    }

    [Fact]
    public async Task Tags_CountOnlyDomainsInAScopedKeysGroups()
    {
        var ownGroupId = await _host.SeedGroupAsync("api-tags-own");
        var otherGroupId = await _host.SeedGroupAsync("api-tags-other");
        var tagId = await _host.SeedTagAsync("api-tags-shared");
        await _host.SeedDomainAsync("api-tags-own.example", groupIds: [ownGroupId], tagIds: [tagId]);
        await _host.SeedDomainAsync("api-tags-other.example", groupIds: [otherGroupId], tagIds: [tagId]);
        var (_, scopedSecret) = await _host.CreateKeyAsync([Permission.TagsView], scopedGroupIds: [ownGroupId]);
        var (_, unscopedSecret) = await _host.CreateKeyAsync([Permission.TagsView]);
        using var scopedClient = _host.ClientFor(scopedSecret);
        using var unscopedClient = _host.ClientFor(unscopedSecret);

        var scopedTag = (await scopedClient.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags"))!.Single(tag => tag.Id == tagId);
        var unscopedTag = (await unscopedClient.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags"))!.Single(tag => tag.Id == tagId);

        Assert.Equal(1, scopedTag.DomainCount);
        Assert.Equal(2, unscopedTag.DomainCount);
        Assert.Equal("Primary", unscopedTag.Color);
    }

    [Fact]
    public async Task GroupsAndTags_CanBeSearchedByName()
    {
        await _host.SeedGroupAsync("api-search-Contoso");
        await _host.SeedTagAsync("api-search-Microsoft 365");
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView, Permission.TagsView]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups?search=SEARCH-cont");
        var tags = await client.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags?search=microsoft");

        Assert.Equal(["api-search-Contoso"], groups!.Select(group => group.Name));
        Assert.Contains("api-search-Microsoft 365", tags!.Select(tag => tag.Name));
        Assert.All(tags!, tag => Assert.Contains("microsoft", tag.Name, StringComparison.OrdinalIgnoreCase));
    }
}
