using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Psa.NinjaOne;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Psa.NinjaOne;

[Collection("Postgres")]
public sealed class NinjaOneSettingsServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public NinjaOneSettingsServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private static NinjaOneSettings Complete(Action<NinjaOneSettings>? change = null)
    {
        var settings = new NinjaOneSettings
        {
            Enabled = true, Instance = "eu.ninjarmm.com", ClientId = "client-1", TicketFormId = 1, TicketFormName = "Default",
            NewStatusId = 1000, NewStatusName = "New", TicketType = "PROBLEM", Priority = "LOW", Severity = "MINOR",
        };
        change?.Invoke(settings);
        return settings;
    }

    private async Task<FakeSecretStore> SaveConnectedAsync()
    {
        var secrets = new FakeSecretStore();
        await using var context = CreateContext();
        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), "secret");
        await NinjaOneSettingsService.ConnectAsync(context, TestActors.Admin, secrets, "refresh-1", Now);
        return secrets;
    }

    [Fact]
    public async Task TheSeededRow_StartsAtNew_WithProblemNoneNone()
    {
        await using var context = CreateContext();
        var settings = await NinjaOneSettingsService.GetAsync(context);

        Assert.Equal((1000, "New", "PROBLEM", "NONE", "NONE", false),
            (settings.NewStatusId, settings.NewStatusName, settings.TicketType, settings.Priority, settings.Severity, settings.IsConnected));
    }

    [Fact]
    public async Task Save_StoresTheClientSecretInTheSecretStore_AndAuditsItAsASecret()
    {
        var secrets = new FakeSecretStore();
        await using var context = CreateContext();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), " secret ");

        Assert.Equal("secret", secrets.Secrets[NinjaOneSettings.ClientSecretKey]);
        await using var verify = CreateContext();
        Assert.True((await NinjaOneSettingsService.GetAsync(verify)).ClientSecretConfigured);
        var entry = await verify.AuditEntries.SingleAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneSettingsSaved);
        Assert.Contains(entry.Changes, change => change.Field == "Client secret");
        Assert.DoesNotContain(entry.Changes, change => change.New == "secret");
    }

    [Fact]
    public async Task Connect_StoresTheRefreshToken_AndRecordsWhoConnected()
    {
        var secrets = await SaveConnectedAsync();

        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.True(saved.IsConnected);
        Assert.Equal((TestActors.Admin.Name, Now), (saved.ConnectedBy, saved.ConnectedUtc));
        Assert.True(await verify.AuditEntries.AnyAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneConnected));
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("client")]
    [InlineData("secret")]
    public async Task ChangingWhatSignsIn_ResetsTheConnection(string change)
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        var updated = Complete(settings =>
        {
            if (change == "instance") settings.Instance = "app.ninjarmm.com";
            if (change == "client") settings.ClientId = "client-2";
        });
        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, updated, change == "secret" ? "new-secret" : null);

        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.False(saved.RefreshTokenConfigured);
        Assert.Null(saved.ConnectedBy);
        Assert.Equal(NinjaOneSettings.ClearedRefreshToken, secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        Assert.Contains("reset the connection", (await verify.AuditEntries.OrderBy(auditEntry => auditEntry.Id).LastAsync()).Summary);
    }

    [Fact]
    public async Task SavingTicketDefaultsOnly_KeepsTheConnection()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(settings => settings.Priority = "HIGH"), null);

        await using var verify = CreateContext();
        var saved = await NinjaOneSettingsService.GetAsync(verify);
        Assert.True(saved.IsConnected);
        Assert.Equal("HIGH", saved.Priority);
        Assert.Equal("refresh-1", secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
    }

    [Fact]
    public async Task SavingWithNoChanges_WritesNothing()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();
        var before = await context.AuditEntries.CountAsync();

        await NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, secrets, Complete(), null);

        Assert.Equal(before, await context.AuditEntries.CountAsync());
        Assert.True((await NinjaOneSettingsService.GetAsync(context)).IsConnected);
    }

    [Fact]
    public async Task Disconnect_ClearsTheTokenAndTheConnection()
    {
        var secrets = await SaveConnectedAsync();
        await using var context = CreateContext();

        await NinjaOneSettingsService.DisconnectAsync(context, TestActors.Admin, secrets);

        await using var verify = CreateContext();
        Assert.False((await NinjaOneSettingsService.GetAsync(verify)).RefreshTokenConfigured);
        Assert.Equal(NinjaOneSettings.ClearedRefreshToken, secrets.Secrets[NinjaOneSettings.RefreshTokenKey]);
        Assert.True(await verify.AuditEntries.AnyAsync(auditEntry => auditEntry.Action == AuditActions.NinjaOneDisconnected));
    }

    [Theory]
    [InlineData("instance", "example.com")]
    [InlineData("type", "BUG")]
    [InlineData("priority", "CRITICAL")]
    [InlineData("severity", "URGENT")]
    public async Task Save_RefusesValuesNinjaOneDoesNotHave(string field, string value)
    {
        await using var context = CreateContext();
        var updated = Complete(settings =>
        {
            if (field == "instance") settings.Instance = value;
            if (field == "type") settings.TicketType = value;
            if (field == "priority") settings.Priority = value;
            if (field == "severity") settings.Severity = value;
        });

        await Assert.ThrowsAsync<ArgumentException>(() => NinjaOneSettingsService.SaveAsync(context, TestActors.Admin, new FakeSecretStore(), updated, null));
    }
}
