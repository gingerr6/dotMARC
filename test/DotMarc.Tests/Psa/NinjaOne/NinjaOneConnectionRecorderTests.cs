using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneConnectionRecorderTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneConnectionRecorderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        settings.RefreshTokenConfigured = true;
        await context.SaveChangesAsync();
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
    public async Task ALostConnection_IsSavedAndAuditedOnce()
    {
        var recorder = new NinjaOneConnectionRecorder(new FakeDbContextFactory(_connectionString), new FixedTimeProvider(Now));

        await recorder.ConnectionLostAsync("NinjaOne refused the sign-in (400): invalid_grant");
        await recorder.ConnectionLostAsync("again");

        await using var verify = CreateContext();
        var settings = await verify.NinjaOneSettings.SingleAsync();
        Assert.Equal((Now, "NinjaOne refused the sign-in (400): invalid_grant"), (settings.ConnectionLostUtc, settings.ConnectionLostReason));
        Assert.Equal(1, await verify.AuditEntries.CountAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneConnectionLost));
    }

    [Fact]
    public async Task ARefresh_IsTimestamped()
    {
        var recorder = new NinjaOneConnectionRecorder(new FakeDbContextFactory(_connectionString), new FixedTimeProvider(Now));

        await recorder.RefreshedAsync();

        await using var verify = CreateContext();
        Assert.Equal(Now, (await verify.NinjaOneSettings.SingleAsync()).LastRefreshUtc);
    }
}
