using System.Net;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Demo;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Components;

/// <summary>The Sources tab hides the AI switch and Suggest button until the person's role allows suggestions and they
/// turn their own switch on. The demo host is used so the test can sign in; the demo flag is then turned off on the
/// running options so a configured provider is allowed to show, which the public demo never does.</summary>
[Collection("Postgres")]
public sealed class SourceSuggestionScreenTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private IAsyncDisposable? _cleanup;
    private string _connectionString = "";
    private WebApplicationFactory<Program>? _factory;

    public SourceSuggestionScreenTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task TheSourcesTab_HidesSuggestions_UntilTheRoleAllowsThem_AndThePersonTurnsThemOn()
    {
        using (await SignInAsync("admin"))
        {
            // Boots the demo, which seeds the domains and the Viewer role.
        }

        _factory!.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled = false;
        await using (var context = CreateContext())
        {
            var settings = await context.SourceSuggestionSettings.SingleAsync();
            settings.Enabled = true;
            settings.Endpoint = "https://models.example/v1";
            settings.Model = "test-model";
            settings.ApiKeyConfigured = true;
            await UserPreferenceService.SetSourceSuggestionsEnabledAsync(context,
                AuditActor.ForUser(null, DemoDataSeeder.ViewerEmail, "Demo Viewer"), true);
            await context.SaveChangesAsync();
        }

        using (var viewer = await SignInAsync("viewer"))
        {
            var response = await viewer.GetAsync("/domains/aurora-retail.example/sources");
            var hidden = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"Sources page returned {(int)response.StatusCode}: {hidden[..Math.Min(hidden.Length, 500)]}");
            Assert.DoesNotContain("AI suggestions", hidden);
            Assert.DoesNotContain(">Suggest<", hidden);
        }

        await using (var context = CreateContext())
        {
            var grant = await context.UserAccesses.Include(access => access.Role).SingleAsync(access => access.Email == DemoDataSeeder.ViewerEmail);
            grant.Role.Permissions = [.. grant.Role.Permissions, Permission.SourceSuggestions];
            await context.SaveChangesAsync();
        }

        using (var viewer = await SignInAsync("viewer"))
        {
            var shown = await viewer.GetStringAsync("/domains/aurora-retail.example/sources");
            Assert.Contains("AI suggestions", shown);
            Assert.Contains(">Suggest<", shown);
        }

        await using (var context = CreateContext())
        {
            await UserPreferenceService.SetSourceSuggestionsEnabledAsync(context,
                AuditActor.ForUser(null, DemoDataSeeder.ViewerEmail, "Demo Viewer"), false);
        }

        using (var viewer = await SignInAsync("viewer"))
        {
            var switchOnly = await viewer.GetStringAsync("/domains/aurora-retail.example/sources");
            Assert.Contains("AI suggestions", switchOnly);
            Assert.DoesNotContain(">Suggest<", switchOnly);
        }
    }

    [Fact]
    public async Task TheClientPortal_DoesNotShowSuggestions_EvenWhenTheRoleAllowsThem()
    {
        using (await SignInAsync("admin"))
        {
        }

        _factory!.Services.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled = false;
        await using (var context = CreateContext())
        {
            var grant = await context.UserAccesses.Include(access => access.Role).SingleAsync(access => access.Email == DemoDataSeeder.ClientEmail);
            grant.Role.Permissions = [.. grant.Role.Permissions, Permission.SourceSuggestions];
            var settings = await context.SourceSuggestionSettings.SingleAsync();
            settings.Enabled = true;
            settings.Endpoint = "https://models.example/v1";
            settings.Model = "test-model";
            settings.ApiKeyConfigured = true;
            await context.SaveChangesAsync();
        }

        using var client = await SignInAsync("client");
        var domain = await client.GetAsync("/domains/aurora-retail.example/sources");
        Assert.Equal(HttpStatusCode.Redirect, domain.StatusCode);

        var portal = await client.GetStringAsync("/portal/domains/aurora-retail.example");
        Assert.Contains("Who sends as this domain", portal);
        Assert.DoesNotContain("AI suggestions", portal);
        Assert.DoesNotContain(">Suggest<", portal);
    }

}
