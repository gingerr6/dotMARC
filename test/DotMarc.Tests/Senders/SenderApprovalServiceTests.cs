using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Senders;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Senders;

[Collection("Postgres")]
public sealed class SenderApprovalServiceTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public SenderApprovalServiceTests(PostgresContainerFixture fixture) => _fixture = fixture;

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

    private async Task<int> SeedDomainAsync()
    {
        await using var context = CreateContext();
        var domain = new Domain { Name = "contoso.io", FirstSeenUtc = DateTimeOffset.UtcNow };
        context.Domains.Add(domain);
        await context.SaveChangesAsync();
        return domain.Id;
    }

    [Fact]
    public async Task SetAsync_RecordsChangesAndClearsADecision_AuditingEach()
    {
        var domainId = await SeedDomainAsync();

        await using (var context = CreateContext())
        {
            await SenderApprovalService.SetAsync(context, TestActors.Admin, domainId, "microsoft365", SenderApprovalStatus.Approved);
            await SenderApprovalService.SetAsync(context, TestActors.Admin, domainId, "microsoft365", SenderApprovalStatus.Approved);
            await SenderApprovalService.SetAsync(context, TestActors.Admin, domainId, "ip:203.0.113.5", SenderApprovalStatus.NotOurs);
        }

        await using (var context = CreateContext())
        {
            var approvals = await SenderApprovalService.GetAsync(context, domainId);
            Assert.Equal(SenderApprovalStatus.Approved, approvals["microsoft365"].Status);
            Assert.Equal(SenderApprovalStatus.NotOurs, approvals["ip:203.0.113.5"].Status);

            await SenderApprovalService.SetAsync(context, TestActors.Admin, domainId, "ip:203.0.113.5", null);
        }

        await using var verify = CreateContext();
        Assert.Equal(["microsoft365"], (await SenderApprovalService.GetAsync(verify, domainId)).Keys);
        var audited = await verify.AuditEntries.Where(entry => entry.Action == AuditActions.DomainSenderReviewed).OrderBy(entry => entry.Id).Select(entry => entry.Summary).ToListAsync();
        Assert.Equal(
            ["Marked Microsoft 365 as approved for contoso.io", "Marked 203.0.113.5 as not ours for contoso.io", "Marked 203.0.113.5 as unreviewed for contoso.io"],
            audited);
    }

    [Fact]
    public async Task RemovingTheDomain_RemovesItsDecisions()
    {
        var domainId = await SeedDomainAsync();
        await using (var context = CreateContext())
        {
            await SenderApprovalService.SetAsync(context, TestActors.Admin, domainId, "google", SenderApprovalStatus.Approved);
            await DomainManagementService.RemoveDomainAsync(context, TestActors.Admin, domainId);
        }

        await using var verify = CreateContext();
        Assert.Empty(verify.SenderApprovals);
    }
}
