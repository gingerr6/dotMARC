using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

[Collection("Postgres")]
public sealed class IpHostNameServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public IpHostNameServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private sealed class StubLookup(Func<string, string?> answer) : IReverseDnsLookup
    {
        public int Calls { get; private set; }

        public Task<string?> LookupAsync(string ip, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(answer(ip));
        }
    }

    [Theory]
    [InlineData(null, 0, true)]                 // never looked up
    [InlineData("mx.example", -29 * 24, false)] // a name, within a month
    [InlineData("mx.example", -31 * 24, true)]  // a name, older than a month
    [InlineData(null, -1, false)]               // no name, within a day
    [InlineData(null, -25, true)]               // no name, older than a day
    public void NeedsLookup_FollowsTheRetryWindows(string? hostName, int ageHours, bool expected)
    {
        var cached = ageHours == 0 && hostName is null
            ? null
            : new IpHostName { Ip = "203.0.113.1", HostName = hostName, LookedUpUtc = DateTimeOffset.UtcNow.AddHours(ageHours) };

        Assert.Equal(expected, IpHostNameService.NeedsLookup(cached, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task EnrichAsync_CachesTheName()
    {
        var lookup = new StubLookup(_ => "mx.example");

        var result = await IpHostNameService.EnrichAsync(new FakeDbContextFactory(_connectionString), lookup, "203.0.113.1", CancellationToken.None);

        Assert.Equal("mx.example", result.HostName);
        await using var verify = CreateContext();
        var cached = await IpHostNameService.GetCachedAsync(verify, ["203.0.113.1"], CancellationToken.None);
        Assert.Equal("mx.example", cached["203.0.113.1"].HostName);
    }

    [Fact]
    public async Task EnrichAsync_StoresAnUnansweredLookupAsNoName()
    {
        var lookup = new ThrowingLookup();

        var result = await IpHostNameService.EnrichAsync(new FakeDbContextFactory(_connectionString), lookup, "203.0.113.2", CancellationToken.None);

        Assert.Null(result.HostName);
        await using var verify = CreateContext();
        Assert.Single(verify.IpHostNames);
    }

    [Fact]
    public async Task EnrichAsync_UpdatesAnExistingRow()
    {
        var factory = new FakeDbContextFactory(_connectionString);
        await IpHostNameService.EnrichAsync(factory, new StubLookup(_ => null), "203.0.113.3", CancellationToken.None);

        await IpHostNameService.EnrichAsync(factory, new StubLookup(_ => "mx.example"), "203.0.113.3", CancellationToken.None);

        await using var verify = CreateContext();
        Assert.Equal("mx.example", (await verify.IpHostNames.SingleAsync()).HostName);
    }

    private sealed class ThrowingLookup : IReverseDnsLookup
    {
        public Task<string?> LookupAsync(string ip, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The DNS resolver couldn't look it up.");
    }
}
