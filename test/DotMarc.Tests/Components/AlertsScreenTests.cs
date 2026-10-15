using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Components;

[Collection("Postgres")]
public sealed class AlertsScreenTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private string _connectionString = "";
    private WebApplicationFactory<Program>? _factory;

    public AlertsScreenTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        var connectionString = _connectionString;
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:DotMarc", connectionString);
            builder.UseSetting("Demo:Enabled", "true");
        });
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_cleanup is not null)
        {
            await _cleanup.DisposeAsync();
        }
    }

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    /// <summary>Raises one alert on a domain in the demo viewer's group and one on a domain outside it, returning both
    /// alert titles.</summary>
    private async Task<(string Inside, string Outside)> RaiseAlertsAsync()
    {
        await using var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);
        var groupId = await context.Groups.Where(group => group.Name == DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName).Select(group => group.Id).SingleAsync();
        var inside = await context.Domains.Where(domain => domain.Groups.Any(group => group.Id == groupId)).Select(domain => domain.Name).FirstAsync();
        var outside = await context.Domains.Where(domain => domain.Groups.All(group => group.Id != groupId)).Select(domain => domain.Name).FirstAsync();
        context.AlertEvents.AddRange(
            new AlertEvent { DomainName = inside, AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "Inside alert", Message = "Alert inside the viewer's group" },
            new AlertEvent { DomainName = outside, AlertType = AlertTypes.DmarcPolicyWeakened, Severity = "Warning", Title = "Outside alert", Message = "Alert outside the viewer's group" });
        await context.SaveChangesAsync();
        return ("Alert inside the viewer's group", "Alert outside the viewer's group");
    }

    [Fact]
    public async Task AViewerLimitedToAGroup_SeesOnlyThatGroupsAlerts()
    {
        using var client = await SignInAsync("viewer"); // boots the demo, which seeds its domains and groups
        var (inside, outside) = await RaiseAlertsAsync();

        var html = await client.GetStringAsync("/alerts");

        Assert.Contains(inside.Replace("'", "&#x27;"), html);
        Assert.DoesNotContain(outside.Replace("'", "&#x27;"), html);
    }

    [Fact]
    public async Task AnAdmin_SeesEveryGroupsAlerts()
    {
        using var client = await SignInAsync("admin");
        var (inside, outside) = await RaiseAlertsAsync();

        var html = await client.GetStringAsync("/alerts");

        Assert.Contains(inside.Replace("'", "&#x27;"), html);
        Assert.Contains(outside.Replace("'", "&#x27;"), html);
    }
}
