using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class TlsReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public TlsReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static TlsrptReport TlsReport(string org, DateTimeOffset receivedUtc, long successful, long failed, string? failureType = null) => new()
    {
        ReportingOrg = org, ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
        RawJson = $$"""{"organization-name":"{{org}}"}""", ReceivedUtc = receivedUtc,
        Policies =
        [
            new TlsrptReportPolicy
            {
                PolicyType = "sts", PolicyDomain = "api-tls.example", SuccessfulSessionCount = successful, FailedSessionCount = failed,
                FailureDetails = failureType is null ? [] : [new TlsrptFailureDetail { ResultType = failureType, FailedSessionCount = failed, ReceivingMxHostname = "mx.api-tls.example" }],
            },
        ],
    };

    [Fact]
    public async Task TlsReports_ComeNewestFirst_AndFilter()
    {
        var domainId = await _host.SeedDomainAsync("api-tls.example");
        await using (var context = _host.CreateContext())
        {
            context.TlsrptReports.Add(Attach(TlsReport("google.com", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40, 0), domainId));
            context.TlsrptReports.Add(Attach(TlsReport("Microsoft", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 30, 3, "certificate-expired"), domainId));
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports");
        var failures = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports?failuresOnly=true");
        var expired = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports?resultType=CERTIFICATE-EXPIRED");

        Assert.Equal(["Microsoft", "google.com"], all!.Items.Select(report => report.ReportingOrg));
        Assert.Equal((30L, 3L), (all.Items[0].SuccessfulSessions, all.Items[0].FailedSessions));
        Assert.Equal("mx.api-tls.example", all.Items[0].Policies.Single().Failures.Single().ReceivingMx);
        Assert.Equal(["Microsoft"], failures!.Items.Select(report => report.ReportingOrg));
        Assert.Equal(["Microsoft"], expired!.Items.Select(report => report.ReportingOrg));
    }

    private static TlsrptReport Attach(TlsrptReport report, int domainId)
    {
        report.DomainId = domainId;
        return report;
    }

    [Fact]
    public async Task OneTlsReport_AndItsRawJson_AndAnotherDomainsIdIsA404()
    {
        var domainId = await _host.SeedDomainAsync("api-tls-one.example");
        var otherId = await _host.SeedDomainAsync("api-tls-other.example");
        int reportId, otherReportId;
        await using (var context = _host.CreateContext())
        {
            var report = Attach(TlsReport("google.com", DateTimeOffset.UtcNow, 5, 0), domainId);
            var other = Attach(TlsReport("yahoo.com", DateTimeOffset.UtcNow, 5, 0), otherId);
            context.TlsrptReports.AddRange(report, other);
            await context.SaveChangesAsync();
            (reportId, otherReportId) = (report.Id, other.Id);
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var one = await client.GetFromJsonAsync<ApiTlsReport>($"/api/v1/domains/api-tls-one.example/tls-reports/{reportId}");
        var raw = await client.GetAsync($"/api/v1/domains/api-tls-one.example/tls-reports/{reportId}/raw");
        var crossed = await client.GetAsync($"/api/v1/domains/api-tls-one.example/tls-reports/{otherReportId}/raw");

        Assert.Equal(reportId, one!.Id);
        Assert.Equal("application/json", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"organization-name":"google.com"}""", await raw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }
}
