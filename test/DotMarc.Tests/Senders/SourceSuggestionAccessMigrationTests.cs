using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DotMarc.Tests.Senders;

/// <summary>Upgrading drops the personal switch and the role permission. A role that still named SourceSuggestions
/// would fail to load, and Suggest would stay hidden behind a permission that no longer means anything.</summary>
[Collection("Postgres")]
public sealed class SourceSuggestionAccessMigrationTests(PostgresContainerFixture fixture)
{
    private const string MigrationBefore = "20261112102509_AddSourceSuggestions";

    [Fact]
    public async Task Upgrading_DropsThePersonalSwitch_AndTheRolePermission()
    {
        var (connectionString, cleanup) = await fixture.CreateDatabaseAsync();
        await using (cleanup)
        {
            await using (var context = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options))
            {
                await context.GetService<IMigrator>().MigrateAsync(MigrationBefore);
                await context.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "Roles" ("Name", "IsLocked", "IsScopable", "Permissions")
                    VALUES
                        ('Had suggestion', false, false, ARRAY['DomainsView', 'SourceSuggestions']::text[]),
                        ('Only suggestion', false, true, ARRAY['SourceSuggestions']::text[]),
                        ('Unchanged', false, false, ARRAY['DomainsView']::text[]);
                    INSERT INTO "UserPreferences" ("UserKey", "SourceSuggestionsEnabled") VALUES ('oid:sam', true);
                    """);
                await context.Database.MigrateAsync();
            }

            await using var verify = new DotMarcDbContext(new DbContextOptionsBuilder<DotMarcDbContext>().UseNpgsql(connectionString).Options);
            var roles = await verify.Roles.OrderBy(role => role.Name).ToListAsync();
            Assert.Equal(["DomainsView"], roles.Single(role => role.Name == "Had suggestion").Permissions.Select(permission => permission.ToString()));
            Assert.Empty(roles.Single(role => role.Name == "Only suggestion").Permissions);
            Assert.Equal(["DomainsView"], roles.Single(role => role.Name == "Unchanged").Permissions.Select(permission => permission.ToString()));
            Assert.False(Enum.TryParse<Permission>("SourceSuggestions", out _));

            await using var connection = verify.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """SELECT to_regclass('public."UserPreferences"') IS NULL""";
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
        }
    }
}
