using DotMarc.Data;
using DotMarc.Setup;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Setup;

[Collection("Postgres")]
public sealed class ClientOnboardingTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ClientOnboardingTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        (_connectionString, _cleanup) = await _fixture.CreateDatabaseAsync();
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        context.Roles.Add(new Role { Name = ClientOnboarding.PortalRoleName, IsScopable = true, Permissions = [Permission.DomainsView] });
        context.Domains.Add(new Domain { Name = "already.example", FirstSeenUtc = DateTimeOffset.UtcNow, Groups = [new Group { Name = "Other client" }] });
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
    public async Task CreatesTheGroup_ItsDomains_AndItsPortalUsers()
    {
        NewClientResult result;
        await using (var context = CreateContext())
        {
            result = await ClientOnboarding.CreateAsync(context, TestActors.Admin,
                new NewClientRequest(" Contoso ", ["Contoso.com", "contoso.co.uk", "contoso.com", "not a domain", "already.example"], ClientManagesDns: true, ["it@contoso.com"]),
                mayGrantAccess: true);
        }

        Assert.Null(result.Refusal);
        Assert.Equal(["contoso.com", "contoso.co.uk"], result.AddedDomains);
        Assert.Equal(["already.example"], result.MovedDomains);
        Assert.Equal(["it@contoso.com"], result.PortalUsers);
        Assert.Single(result.Problems, problem => problem.Contains("not a domain"));

        await using var verify = CreateContext();
        var group = await verify.Groups.Include(g => g.Domains).SingleAsync(g => g.Id == result.GroupId);
        Assert.Equal("Contoso", group.Name);
        Assert.True(group.ClientManagesDns);
        Assert.Equal(["already.example", "contoso.co.uk", "contoso.com"], group.Domains.Select(d => d.Name).Order());
        var moved = await verify.Domains.Include(d => d.Groups).SingleAsync(d => d.Name == "already.example");
        Assert.Equal(["Contoso", "Other client"], moved.Groups.Select(g => g.Name).Order());

        var access = await verify.UserAccesses.Include(a => a.Role).Include(a => a.ScopedGroups).SingleAsync(a => a.Email == "it@contoso.com");
        Assert.True(access.IsClientPortal);
        Assert.Equal(ClientOnboarding.PortalRoleName, access.Role.Name);
        Assert.Equal([group.Id], access.ScopedGroups.Select(g => g.Id));
    }

    [Fact]
    public async Task RefusesAGroupThatAlreadyExists_ChangingNothing()
    {
        await using var context = CreateContext();

        var result = await ClientOnboarding.CreateAsync(context, TestActors.Admin,
            new NewClientRequest("other CLIENT", ["new.example"], false, []), mayGrantAccess: true);

        Assert.NotNull(result.Refusal);
        Assert.False(await context.Domains.AnyAsync(d => d.Name == "new.example"));
    }

    [Fact]
    public async Task WithoutAccessManage_AddsNoPortalUsers()
    {
        await using var context = CreateContext();

        var result = await ClientOnboarding.CreateAsync(context, TestActors.Admin,
            new NewClientRequest("Fabrikam", ["fabrikam.com"], false, ["it@fabrikam.com"]), mayGrantAccess: false);

        Assert.Empty(result.PortalUsers);
        Assert.NotEmpty(result.Problems);
        Assert.False(await context.UserAccesses.AnyAsync(a => a.Email == "it@fabrikam.com"));
    }
}
