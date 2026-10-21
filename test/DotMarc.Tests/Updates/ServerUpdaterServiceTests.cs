using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using DotMarc.Updates;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Updates;

[Collection("Postgres")]
public sealed class ServerUpdaterServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ServerUpdaterServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext() => new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    [Fact]
    public async Task RequestAsync_SetsTheRequestForTheServerAndAudits()
    {
        await using (var context = CreateContext())
        {
            await ServerUpdaterService.RequestAsync(context, AuditActor.ForUser("oid", "jo@example.com", "Jo"), Now);
        }

        await using var check = CreateContext();
        var updater = await ServerUpdaterService.GetAsync(check);
        Assert.Equal((Now, "Jo"), (updater.RequestedUtc, updater.RequestedBy));
        Assert.Equal(AuditActions.ServerUpdateRequested, (await check.AuditEntries.SingleAsync()).Action);
    }

    [Fact]
    public void IsRunning_OnlyWhenTheUpdaterCheckedInRecently()
    {
        Assert.False(ServerUpdaterService.IsRunning(new ServerUpdater(), Now));
        Assert.True(ServerUpdaterService.IsRunning(new ServerUpdater { LastSeenUtc = Now.AddMinutes(-1) }, Now));
        Assert.False(ServerUpdaterService.IsRunning(new ServerUpdater { LastSeenUtc = Now.AddMinutes(-10) }, Now));
    }
}
