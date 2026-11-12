using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Senders;

[Collection("Postgres")]
public sealed class UserPreferenceServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public UserPreferenceServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    [Fact]
    public async Task Suggestions_DefaultOff_AndEachPersonChoosesForThemselves()
    {
        var sam = AuditActor.ForUser("oid-sam", "sam@example.com", "Sam");
        var jo = AuditActor.ForUser("oid-jo", "jo@example.com", "Jo");
        await using var context = CreateContext();

        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(context, sam));
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(context, jo));

        await UserPreferenceService.SetSourceSuggestionsEnabledAsync(context, sam, true);

        await using var verify = CreateContext();
        Assert.True(await UserPreferenceService.SourceSuggestionsEnabledAsync(verify, sam));
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(verify, jo));

        await UserPreferenceService.SetSourceSuggestionsEnabledAsync(verify, sam, false);
        await using var after = CreateContext();
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(after, sam));
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(after, jo));
    }

    [Fact]
    public async Task TwoPeople_WithOnlyAnEmail_DoNotShareAPreference()
    {
        var sam = AuditActor.ForUser(null, "sam@example.com", "Sam");
        var jo = AuditActor.ForUser(null, "jo@example.com", "Jo");
        await using var context = CreateContext();

        await UserPreferenceService.SetSourceSuggestionsEnabledAsync(context, sam, true);

        await using var verify = CreateContext();
        Assert.True(await UserPreferenceService.SourceSuggestionsEnabledAsync(verify, sam));
        Assert.False(await UserPreferenceService.SourceSuggestionsEnabledAsync(verify, jo));
    }
}
