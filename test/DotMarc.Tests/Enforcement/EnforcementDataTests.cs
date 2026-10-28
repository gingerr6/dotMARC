using DotMarc.Data;
using DotMarc.Enforcement;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Enforcement;

[Collection("Postgres")]
public sealed class EnforcementDataTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private string _connectionString = "";
    private IAsyncDisposable? _cleanup;

    public EnforcementDataTests(PostgresContainerFixture fixture) => _fixture = fixture;

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
    public async Task A_source_forwarding_the_domains_signed_mail_is_not_waiting_on_review()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var seed = CreateContext())
        {
            var domain = new Domain { Name = "contoso.com", IsMonitored = true, FirstSeenUtc = now.AddDays(-20), DmarcPolicy = DmarcPolicyLevel.None, DmarcCheckStatus = DmarcCheckStatus.Ok };
            var report = new Report { ReportingOrg = "google.com", ReportId = "1", DateRangeBeginUtc = now.AddDays(-2), DateRangeEndUtc = now.AddDays(-1), RawXml = "<feedback/>", ReceivedUtc = now.AddDays(-1) };
            var forwarded = new ReportRecord { SourceIp = "203.0.113.20", MessageCount = 3, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail, Disposition = DispositionResult.None, HeaderFrom = "contoso.com" };
            forwarded.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "contoso.com", Result = DmarcMechanismResult.Fail });
            forwarded.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Spf, Domain = "contoso.com", Result = DmarcMechanismResult.Fail });
            var spoofed = new ReportRecord { SourceIp = "198.51.100.66", MessageCount = 3, SpfResult = AuthResult.Fail, DkimResult = AuthResult.Fail, Disposition = DispositionResult.None, HeaderFrom = "contoso.com" };
            spoofed.AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Spf, Domain = "contoso.com", Result = DmarcMechanismResult.Fail });
            report.Records.AddRange([forwarded, spoofed]);
            domain.Reports.Add(report);
            seed.Domains.Add(domain);
            await seed.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var domains = await db.Domains.ToListAsync();
        var assessment = (await EnforcementData.AssessAsync(db, domains, now))[domains[0].Id];

        var unreviewed = Assert.Single(assessment.Blockers, blocker => blocker.Title.Contains("not reviewed"));
        Assert.Contains("198.51.100.66", unreviewed.Detail);
        Assert.DoesNotContain("203.0.113.20", unreviewed.Detail);
    }
}
