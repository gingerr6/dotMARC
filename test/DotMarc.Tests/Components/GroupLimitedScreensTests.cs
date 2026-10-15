using System.Net;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The demo viewer, switched to a custom role limited to Groups that can add, edit and remove domains: what a
/// member of staff who looks after one client would get.</summary>
[Collection("Postgres")]
public sealed class GroupLimitedScreensTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private string _connectionString = "";
    private WebApplicationFactory<Program>? _factory;

    public GroupLimitedScreensTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).Options);

    private async Task<HttpClient> SignInAsync(string persona)
    {
        var client = _factory!.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync($"/demo/sign-in/{persona}", content: null);
        return client;
    }

    /// <summary>Boots the demo, gives the demo viewer a limited client manager role, and returns a domain inside their
    /// Group and one outside it.</summary>
    private async Task<(HttpClient Client, Domain Inside, Domain Outside)> SignInAsAClientManagerAsync()
    {
        using (await SignInAsync("admin"))
        {
            // Booting the demo seeds its domains, Groups and grants.
        }

        await using (var context = CreateContext())
        {
            var role = new Role
            {
                Name = "Client manager",
                IsScopable = true,
                Permissions = [Permission.DomainsView, Permission.DomainsAdd, Permission.DomainsEdit, Permission.DomainsDelete, Permission.AlertsView,
                    Permission.AlertsManage, Permission.AuditView, Permission.ReportsManage],
            };
            var grant = await context.UserAccesses.SingleAsync(access => access.Email == DotMarc.Demo.DemoDataSeeder.ViewerEmail);
            grant.Role = role;
            await context.SaveChangesAsync();
        }

        await using var read = CreateContext();
        var groupId = await read.Groups.Where(group => group.Name == DotMarc.Demo.DemoDataSeeder.ViewerScopedGroupName).Select(group => group.Id).SingleAsync();
        var inside = await read.Domains.AsNoTracking().FirstAsync(domain => domain.Groups.Any(group => group.Id == groupId));
        var outside = await read.Domains.AsNoTracking().FirstAsync(domain => domain.Groups.All(group => group.Id != groupId));
        return (await SignInAsync("viewer"), inside, outside);
    }

    [Fact]
    public async Task ManageDomains_ShowsAClientManagerOnlyTheirGroupsDomains()
    {
        var (client, inside, outside) = await SignInAsAClientManagerAsync();
        using (client)
        {
            var response = await client.GetAsync("/domains");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(inside.Name, html);
            Assert.DoesNotContain(outside.Name, html);
            Assert.DoesNotContain("Import domains", html);
        }
    }

    [Fact]
    public async Task ManageDomains_ShowsAnAdminEveryDomainAndImport()
    {
        var (managerClient, inside, outside) = await SignInAsAClientManagerAsync();
        managerClient.Dispose();
        using var client = await SignInAsync("admin");

        var html = await client.GetStringAsync("/domains");

        Assert.Contains(inside.Name, html);
        Assert.Contains(outside.Name, html);
        Assert.Contains("Import domains", html);
    }

    [Theory]
    [InlineData("/domains/import")]
    [InlineData("/domains/import/sample.csv")]
    [InlineData("/parse-failures")]
    [InlineData("/audit")]
    [InlineData("/audit/export")]
    [InlineData("/alerts/settings")]
    [InlineData("/psa/settings")]
    [InlineData("/reports/settings")]
    public async Task AClientManager_CantOpenTenantWidePages(string path)
    {
        var (client, _, _) = await SignInAsAClientManagerAsync();
        using (client)
        {
            var response = await client.GetAsync(path);

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task ADnsPush_ForADomainOutsideTheirGroups_IsForbidden()
    {
        var (client, inside, outside) = await SignInAsAClientManagerAsync();
        using (client)
        {
            var outsideResponse = await StartDmarcPushAsync(client, outside.Id);
            var insideResponse = await StartDmarcPushAsync(client, inside.Id);

            Assert.True(IsForbidden(outsideResponse), $"Expected forbidden, got {(int)outsideResponse.StatusCode} {outsideResponse.Headers.Location}");
            Assert.False(IsForbidden(insideResponse), $"Expected allowed, got {(int)insideResponse.StatusCode} {insideResponse.Headers.Location}");
        }
    }

    private static Task<HttpResponseMessage> StartDmarcPushAsync(HttpClient client, int domainId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/dns-push/cloudflare/start?domainId={domainId}&target=dmarc");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        return client.SendAsync(request);
    }

    // A cookie-authenticated Forbid redirects to the access denied page rather than answering 403.
    private static bool IsForbidden(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Forbidden
        || response.Headers.Location?.ToString().Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) == true;
}
