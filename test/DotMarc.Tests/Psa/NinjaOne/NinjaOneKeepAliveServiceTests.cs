using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneKeepAliveServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneKeepAliveServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task SetUpAsync(bool enabled, bool connected, DateTimeOffset? lostUtc = null)
    {
        await using var context = CreateContext();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured, settings.RefreshTokenConfigured) = (enabled, "eu.ninjarmm.com", "client-1", true, connected);
        (settings.ConnectionLostUtc, settings.ConnectionLostReason) = (lostUtc, lostUtc is null ? null : "invalid_grant");
        await context.SaveChangesAsync();
    }

    /// <summary>Records refresh calls; set Lose to make the refresh fail the way the real client does.</summary>
    private sealed class RefreshingClient(string connectionString) : INinjaOneClient
    {
        public int Refreshes { get; private set; }
        public bool Lose { get; set; }
        public bool Transient { get; set; }

        public async Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default)
        {
            Refreshes++;
            if (Transient)
            {
                throw new HttpRequestException("NinjaOne's sign-in service returned 502.");
            }

            if (Lose)
            {
                await new NinjaOneConnectionRecorder(new FakeDbContextFactory(connectionString), TimeProvider.System).ConnectionLostAsync("invalid_grant", cancellationToken);
                throw new NinjaOneConnectionLostException("invalid_grant");
            }
        }

        public Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Records PSA connection alerts; nothing else is used.</summary>
    private sealed class ConnectionAlerts : IAlertingService
    {
        public List<string> Raised { get; } = [];
        public int Resolves { get; private set; }

        public Task RaisePsaConnectionLostAsync(PsaKind psa, string reason, CancellationToken cancellationToken = default)
        {
            Raised.Add($"{psa}: {reason}");
            return Task.CompletedTask;
        }

        public Task ResolvePsaConnectionLostAsync(PsaKind psa, CancellationToken cancellationToken = default)
        {
            Resolves++;
            return Task.CompletedTask;
        }

        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private NinjaOneKeepAliveService Service(INinjaOneClient client, IAlertingService alerts) =>
        new(new FakeDbContextFactory(_connectionString), client, alerts, NullLogger<NinjaOneKeepAliveService>.Instance);

    [Fact]
    public async Task WhenConnected_ItRefreshes_AndResolvesAnyOldAlert()
    {
        await SetUpAsync(enabled: true, connected: true);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, client.Refreshes);
        Assert.Empty(alerts.Raised);
        Assert.Equal(1, alerts.Resolves);
    }

    [Fact]
    public async Task ARefusedRefresh_RaisesTheAlert()
    {
        await SetUpAsync(enabled: true, connected: true);
        var alerts = new ConnectionAlerts();

        await Service(new RefreshingClient(_connectionString) { Lose = true }, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal("NinjaOne: invalid_grant", Assert.Single(alerts.Raised));
    }

    [Fact]
    public async Task ATransientFailure_RaisesNothing_AndKeepsTheConnection()
    {
        await SetUpAsync(enabled: true, connected: true);
        var alerts = new ConnectionAlerts();

        await Service(new RefreshingClient(_connectionString) { Transient = true }, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Empty(alerts.Raised);
        await using var verify = CreateContext();
        Assert.True((await verify.NinjaOneSettings.SingleAsync()).IsConnected);
    }

    [Fact]
    public async Task ALossFoundByATicketCall_IsRaisedOnTheNextRun_WithoutRefreshing()
    {
        await SetUpAsync(enabled: true, connected: true, lostUtc: DateTimeOffset.UtcNow);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, client.Refreshes);
        Assert.Equal("NinjaOne: invalid_grant", Assert.Single(alerts.Raised));
    }

    [Fact]
    public async Task WhenNinjaOneIsTurnedOff_ItDoesNotRefresh_AndResolvesTheAlert()
    {
        await SetUpAsync(enabled: false, connected: true, lostUtc: DateTimeOffset.UtcNow);
        var client = new RefreshingClient(_connectionString);
        var alerts = new ConnectionAlerts();

        await Service(client, alerts).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, client.Refreshes);
        Assert.Empty(alerts.Raised);
        Assert.Equal(1, alerts.Resolves);
    }

    [Fact]
    public void ItRefreshesEvery20Minutes_TwiceInsideTheHourARefreshTokenLasts()
    {
        Assert.Equal(TimeSpan.FromMinutes(20), NinjaOneKeepAliveService.Interval);
    }
}
