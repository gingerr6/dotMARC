using DotMarc.Data;
using DotMarc.Psa;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneProviderTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneProviderTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        var settings = await context.NinjaOneSettings.SingleAsync();
        (settings.Enabled, settings.Instance, settings.ClientId, settings.ClientSecretConfigured, settings.RefreshTokenConfigured, settings.TicketFormId) =
            (true, "eu.ninjarmm.com", "client-1", true, true, 1);
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

    private sealed class FakeNinjaOneClient : INinjaOneClient
    {
        public NinjaOneTicket? Ticket { get; set; }
        public Exception? CommentFailure { get; set; }
        public List<string> Calls { get; } = [];

        public Task<NinjaOneTicket?> GetTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default) => Task.FromResult(Ticket);

        public Task AddCommentAsync(NinjaOneSettings settings, string ticketId, string body, CancellationToken cancellationToken = default)
        {
            Calls.Add($"comment {ticketId}: {body}");
            return CommentFailure is null ? Task.CompletedTask : Task.FromException(CommentFailure);
        }

        public Task ResolveTicketAsync(NinjaOneSettings settings, string ticketId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"resolve {ticketId}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PsaCompany>> ListOrganizationsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<PsaOption>> ListTicketFormsAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<NinjaOneStatus>> ListStatusesAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateTicketAsync(NinjaOneSettings settings, PsaTicketRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RefreshConnectionAsync(NinjaOneSettings settings, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static NinjaOneTicket Ticket(int statusId, int? parentId = null, bool deleted = false) =>
        new(1004, 1, 1, 1, "SPF broken", "PROBLEM", "NONE", "NONE", "user-1", new NinjaOneTicketStatus(statusId, "X", parentId), deleted);

    [Fact]
    public async Task IsReady_WhenConnectedWithAFormAndStatus()
    {
        await using var context = CreateContext();

        var readiness = await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetReadinessAsync(context);

        Assert.True(readiness.IsReady);
    }

    [Fact]
    public void MissingSettings_NamesEachGap_AndALostConnection()
    {
        var settings = new NinjaOneSettings { RefreshTokenConfigured = true, ConnectionLostUtc = DateTimeOffset.UtcNow, NewStatusId = null };

        Assert.Equal(["instance", "client ID", "client secret", "connection", "ticket form", "new ticket status"], NinjaOneProvider.MissingSettings(settings));
    }

    [Theory]
    [InlineData(1000, null, false, PsaTicketState.Open)]
    [InlineData(5000, null, false, PsaTicketState.Closed)]
    [InlineData(6000, null, false, PsaTicketState.Closed)]
    [InlineData(5001, 5000, false, PsaTicketState.Closed)]
    [InlineData(1000, null, true, PsaTicketState.Missing)]
    public async Task TicketState_FollowsTheStatusOrItsParent(int statusId, int? parentId, bool deleted, PsaTicketState expected)
    {
        await using var context = CreateContext();
        var client = new FakeNinjaOneClient { Ticket = Ticket(statusId, parentId, deleted) };

        Assert.Equal(expected, await new NinjaOneProvider(client, NullLogger<NinjaOneProvider>.Instance).GetTicketStateAsync(context, "1004"));
    }

    [Fact]
    public async Task ATicketNinjaOneDoesNotHave_IsMissing()
    {
        await using var context = CreateContext();

        Assert.Equal(PsaTicketState.Missing, await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetTicketStateAsync(context, "1004"));
    }

    [Fact]
    public async Task Close_AddsTheNoteThenResolves_EvenWhenTheNoteFails()
    {
        await using var context = CreateContext();
        var client = new FakeNinjaOneClient { CommentFailure = new HttpRequestException("comment refused") };

        await new NinjaOneProvider(client, NullLogger<NinjaOneProvider>.Instance).CloseTicketAsync(context, "1004", "Alert resolved.");

        Assert.Equal(["comment 1004: Alert resolved.", "resolve 1004"], client.Calls);
    }

    [Fact]
    public async Task TicketLinks_OpenTheTicketOnTheInstance()
    {
        await using var context = CreateContext();

        Assert.Equal("https://eu.ninjarmm.com/#/ticketing/ticket/{0}",
            await new NinjaOneProvider(new FakeNinjaOneClient(), NullLogger<NinjaOneProvider>.Instance).GetTicketUrlTemplateAsync(context));
    }
}
