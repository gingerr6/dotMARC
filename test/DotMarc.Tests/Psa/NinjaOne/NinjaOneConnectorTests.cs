using System.Net;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneConnectorTests : IAsyncLifetime
{
    private const string RedirectUri = "https://dotmarc.example/psa/ninjaone/callback";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private readonly NinjaOneStateProtector _protector = new(new EphemeralDataProtectionProvider());
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneConnectorTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured) = (true, "eu.ninjarmm.com", "client-1", true);
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

    private (NinjaOneConnector Connector, FakeHttpMessageHandler Handler, FakeSecretStore Secrets, RecordingConnectionAlerts Alerts) Create()
    {
        var handler = new FakeHttpMessageHandler();
        var secrets = new FakeSecretStore { Secrets = { [NinjaOneSettings.ClientSecretKey] = "secret" } };
        var alerts = new RecordingConnectionAlerts();
        var connector = new NinjaOneConnector(new HttpClient(handler), new FakeDbContextFactory(_connectionString), secrets, _protector,
            new NinjaOneTokenCache(secrets, TimeProvider.System), alerts, new FixedTimeProvider(Now), NullLogger<NinjaOneConnector>.Instance);
        return (connector, handler, secrets, alerts);
    }

    private string StateFor(AuditActor actor) => _protector.Protect(NinjaOneStateProtector.UserKey(actor), Now);

    [Fact]
    public async Task BuildsTheAuthorizeUrl_OnceTheSignInIsSaved()
    {
        var (connector, _, _, _) = Create();

        var url = await connector.BuildAuthorizationUrlAsync(RedirectUri, "state-1", CancellationToken.None);

        Assert.StartsWith("https://eu.ninjarmm.com/ws/oauth/authorize?response_type=code&client_id=client-1", url);
    }

    [Fact]
    public async Task ASuccessfulSignIn_StoresTheRefreshToken_AndResolvesTheAlert()
    {
        var (connector, handler, secrets, alerts) = Create();
        handler.ResponseBody = """{"access_token":"access-1","expires_in":3600,"refresh_token":"refresh-1"}""";

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.Connected, outcome);
        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        var form = Assert.Single(handler.RequestBodies);
        Assert.Contains("grant_type=authorization_code", form);
        Assert.Contains("code=code-1", form);
        Assert.Contains("redirect_uri=https%3A%2F%2Fdotmarc.example%2Fpsa%2Fninjaone%2Fcallback", form);
        Assert.Equal(1, alerts.Resolves);
        await using var verify = CreateContext();
        Assert.True((await verify.NinjaOneSettings.SingleAsync()).IsConnected);
    }

    [Fact]
    public async Task AnotherUsersState_IsRefused_AndNothingIsSaved()
    {
        var (connector, handler, secrets, _) = Create();
        var someoneElse = AuditActor.ForUser("other-object-id", "other@example.com", "Someone Else");

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(someoneElse), "code-1", null, RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.InvalidState, outcome);
        Assert.Empty(handler.Requests);
        Assert.False(secrets.Secrets.ContainsKey(NinjaOneSettings.RefreshTokenKey));
    }

    [Fact]
    public async Task ACancelledSignIn_SavesNothing()
    {
        var (connector, handler, _, _) = Create();

        var outcome = await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), null, "access_denied", RedirectUri, CancellationToken.None);

        Assert.Equal(NinjaOneConnectOutcome.Cancelled, outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ARefusedCode_IsReported()
    {
        var (connector, handler, _, _) = Create();
        handler.StatusCode = HttpStatusCode.BadRequest;
        handler.ResponseBody = """{"error":"invalid_grant"}""";

        Assert.Equal(NinjaOneConnectOutcome.Refused,
            await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None));
    }

    [Fact]
    public async Task NoRefreshToken_MeansTheAppIsMissingTheRefreshTokenGrant()
    {
        var (connector, handler, secrets, _) = Create();
        handler.ResponseBody = """{"access_token":"access-1","expires_in":3600}""";

        Assert.Equal(NinjaOneConnectOutcome.NoRefreshToken,
            await connector.CompleteAsync(TestActors.Admin, StateFor(TestActors.Admin), "code-1", null, RedirectUri, CancellationToken.None));
        Assert.False(secrets.Secrets.ContainsKey(NinjaOneSettings.RefreshTokenKey));
    }

    [Theory]
    [InlineData(NinjaOneConnectOutcome.Connected, "connected")]
    [InlineData(NinjaOneConnectOutcome.InvalidState, "expired")]
    [InlineData(NinjaOneConnectOutcome.NoRefreshToken, "no-refresh-token")]
    public void Outcomes_HaveShortKeysForTheSettingsPage(NinjaOneConnectOutcome outcome, string key) =>
        Assert.Equal(key, NinjaOneConnector.OutcomeKey(outcome));

    /// <summary>Counts lost-connection resolves; nothing else is used.</summary>
    private sealed class RecordingConnectionAlerts : DotMarc.Notifications.IAlertingService
    {
        public int Resolves { get; private set; }

        public Task ResolvePsaConnectionLostAsync(PsaKind psa, CancellationToken cancellationToken = default)
        {
            Resolves++;
            return Task.CompletedTask;
        }

        public Task RaisePsaConnectionLostAsync(PsaKind psa, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CheckPinnedDomainsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveDomainAlertAsync(string domainName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task HandleTlsrptReportAsync(string domainName, long failedSessionCount, IReadOnlyList<string> failureTypes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task FlagUnexpectedActivityForNullRoutedDomainAsync(string domainName, DotMarc.Reporting.ReasonBreakdown reasonBreakdown, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RaiseClientReportFailedAsync(int groupId, string groupName, string periodLabel, string error, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResolveClientReportFailedAsync(int groupId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
