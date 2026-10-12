using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ParseFailureEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ParseFailureEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ParseFailures_ComeNewestFirst_AndFilter()
    {
        await using (var context = _host.CreateContext())
        {
            context.ParseFailures.AddRange(
                new ParseFailure { GraphMessageId = "api-pf-1", Reason = "api-pf schema validation", AttemptCount = 5, LastAttemptedUtc = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero) },
                new ParseFailure { GraphMessageId = "api-pf-2", Reason = "api-pf not a report", AttemptCount = 1, LastAttemptedUtc = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero) });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiParseFailure>>("/api/v1/parse-failures?search=api-pf");
        var stubborn = await client.GetFromJsonAsync<ApiPage<ApiParseFailure>>("/api/v1/parse-failures?search=API-PF&minAttempts=3&lastAttemptBefore=2026-09-03");

        Assert.Equal(["api-pf-2", "api-pf-1"], all!.Items.Select(failure => failure.MessageId));
        Assert.Equal(["api-pf-1"], stubborn!.Items.Select(failure => failure.MessageId));
    }

    [Fact]
    public async Task AGroupLimitedKey_IsRefused()
    {
        var group = await _host.SeedGroupAsync("api-pf-group");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [group]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/parse-failures");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("limited to certain groups", await response.Content.ReadAsStringAsync());
    }
}
