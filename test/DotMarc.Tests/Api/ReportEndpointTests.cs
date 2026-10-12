using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportFrom(string org, DateTimeOffset receivedUtc, string xml, params (string SourceIp, int Count, AuthResult Spf, AuthResult Dkim)[] records)
    {
        var report = new Report
        {
            ReportingOrg = org, ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
            RawXml = xml, ReceivedUtc = receivedUtc, AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, count, spf, dkim) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = count, Disposition = DispositionResult.None, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    [Fact]
    public async Task Reports_ComeNewestFirst_WithRecordsAndDetails_AndFilter()
    {
        await _host.SeedDomainAsync("api-reports.example", configure: domain =>
        {
            var google = ReportFrom("google.com", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "<feedback>g</feedback>", ("192.0.2.40", 8, AuthResult.Pass, AuthResult.Pass));
            google.Records[0].AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "api-reports.example", Result = DmarcMechanismResult.Pass, Selector = "s1" });
            domain.Reports.Add(google);
            domain.Reports.Add(ReportFrom("Microsoft Corporation", new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), "<feedback>m</feedback>",
                ("192.0.2.41", 2, AuthResult.Fail, AuthResult.Fail), ("192.0.2.42", 6, AuthResult.Pass, AuthResult.Fail)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports");
        var failing = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports?failingOnly=true");
        var byOrg = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports?reportingOrg=GOOGLE&receivedBefore=2026-09-02");

        Assert.Equal(["Microsoft Corporation", "google.com"], all!.Items.Select(report => report.ReportingOrg));
        Assert.Equal((8, 6d / 8), (all.Items[0].MessageCount, all.Items[0].PassRate!.Value));
        var detail = Assert.Single(all.Items[1].Records).AuthDetails.Single();
        Assert.Equal(("Dkim", "Pass", "s1"), (detail.Mechanism, detail.Result, detail.Selector));
        Assert.Equal(["Microsoft Corporation"], failing!.Items.Select(report => report.ReportingOrg));
        Assert.Equal(["google.com"], byOrg!.Items.Select(report => report.ReportingOrg));
    }

    [Fact]
    public async Task OneReport_AndItsRawXml_AndAnotherDomainsIdIsA404()
    {
        await _host.SeedDomainAsync("api-report-one.example", configure: domain =>
            domain.Reports.Add(ReportFrom("google.com", DateTimeOffset.UtcNow, "<feedback><raw/></feedback>", ("192.0.2.43", 1, AuthResult.Pass, AuthResult.Pass))));
        await _host.SeedDomainAsync("api-report-other.example", configure: domain =>
            domain.Reports.Add(ReportFrom("yahoo.com", DateTimeOffset.UtcNow, "<feedback/>", ("192.0.2.44", 1, AuthResult.Pass, AuthResult.Pass))));
        await using var context = _host.CreateContext();
        var reportId = await context.Reports.Where(report => report.Domain.Name == "api-report-one.example").Select(report => report.Id).SingleAsync();
        var otherId = await context.Reports.Where(report => report.Domain.Name == "api-report-other.example").Select(report => report.Id).SingleAsync();
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var one = await client.GetFromJsonAsync<ApiDmarcReport>($"/api/v1/domains/api-report-one.example/reports/{reportId}");
        var raw = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{reportId}/raw");
        var crossed = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{otherId}");
        var crossedRaw = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{otherId}/raw");

        Assert.Equal(reportId, one!.Id);
        Assert.Equal("application/xml", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<feedback><raw/></feedback>", await raw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossedRaw.StatusCode);
    }
}
