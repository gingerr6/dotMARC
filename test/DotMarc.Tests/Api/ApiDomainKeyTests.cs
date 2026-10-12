using System.Data.Common;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ApiDomainKeyTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public ApiDomainKeyTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private DotMarcDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(_connectionString).AddInterceptors(interceptors).Options);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<int> SeedAsync(string name)
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = name, FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task ResolveAsync_FindsEveryKey_ByIdOrName_InOneQuery()
    {
        var byId = await SeedAsync("keys-by-id.example");
        var byName = await SeedAsync("keys-by-name.example");
        var byUpperName = await SeedAsync("keys-upper.example");
        var counter = new CommandCounter();
        await using var context = CreateContext(counter);

        var resolved = await ApiDomainKey.ResolveAsync(context.Domains,
            [byId.ToString(System.Globalization.CultureInfo.InvariantCulture), "keys-by-name.example", "KEYS-UPPER.EXAMPLE."], CancellationToken.None);

        Assert.Null(resolved.MissingKey);
        Assert.Equal(new[] { byId, byName, byUpperName }.Order(), resolved.Domains.Select(domain => domain.Id).Order());
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task ResolveAsync_NamesTheFirstKeyItCantFind()
    {
        await SeedAsync("keys-present.example");
        await using var context = CreateContext();

        var resolved = await ApiDomainKey.ResolveAsync(context.Domains, ["keys-present.example", "keys-absent.example", "not a domain"], CancellationToken.None);

        Assert.Equal("keys-absent.example", resolved.MissingKey);
    }
}
