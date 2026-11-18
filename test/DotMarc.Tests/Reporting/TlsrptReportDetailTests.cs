using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class TlsrptReportDetailTests
{
    // Google's shape: two versions of the same policy in one day, the older one with the failure.
    private const string TwoPolicies = """
        {
          "organization-name": "Google Inc.",
          "date-range": { "start-datetime": "2026-11-15T00:00:00Z", "end-datetime": "2026-11-15T23:59:59Z" },
          "contact-info": "smtp-tls-reporting@google.com",
          "report-id": "2026-11-15T00:00:00Z_bcdr.co.uk",
          "policies": [
            {
              "policy": {
                "policy-type": "sts",
                "policy-string": ["version: STSv1", "mode: enforce", "mx: bcdr-co-uk.mail.protection.outlook.com", "max_age: 604800"],
                "policy-domain": "bcdr.co.uk"
              },
              "summary": { "total-successful-session-count": 0, "total-failure-session-count": 1 },
              "failure-details": [
                {
                  "result-type": "validation-failure",
                  "sending-mta-ip": "209.85.128.1",
                  "receiving-mx-hostname": "mx.postark.mspcyber.uk",
                  "receiving-ip": "138.68.140.145",
                  "failed-session-count": 1
                }
              ]
            },
            {
              "policy": {
                "policy-type": "sts",
                "policy-string": ["version: STSv1", "mode: enforce", "mx: mx.postark.mspcyber.uk", "mx: bcdr-co-uk.mail.protection.outlook.com", "max_age: 604800"],
                "policy-domain": "bcdr.co.uk"
              },
              "summary": { "total-successful-session-count": 5, "total-failure-session-count": 0 }
            }
          ]
        }
        """;

    [Fact]
    public void EachPolicyVersion_KeepsItsOwnMailServersAndCounts()
    {
        var detail = TlsrptReportDetail.From(Report(TwoPolicies));

        Assert.True(detail.FromRawJson);
        Assert.Equal(2, detail.Policies.Count);
        var older = detail.Policies[0];
        Assert.Equal("enforce", older.Mode);
        Assert.Equal(["bcdr-co-uk.mail.protection.outlook.com"], older.MxHosts);
        Assert.Equal("mode enforce · max_age 604800", older.Settings);
        Assert.Equal((0L, 1L), (older.SuccessfulSessions, older.FailedSessions));
        Assert.Equal(["mx.postark.mspcyber.uk", "bcdr-co-uk.mail.protection.outlook.com"], detail.Policies[1].MxHosts);
        Assert.Equal(5, detail.Policies[1].SuccessfulSessions);
        Assert.Empty(detail.Policies[1].Failures);
    }

    [Fact]
    public void AFailure_SaysWhereItHappened()
    {
        var failure = Assert.Single(TlsrptReportDetail.From(Report(TwoPolicies)).Policies[0].Failures);

        Assert.Equal("validation-failure", failure.ResultType);
        Assert.Equal("at mx.postark.mspcyber.uk (138.68.140.145), from 209.85.128.1", failure.Where);
        Assert.NotNull(TlsrptResultTypes.Explain(failure.ResultType));
    }

    [Fact]
    public void UnreadableJson_FallsBackToTheStoredRows()
    {
        var report = Report("not json");
        report.Policies.Add(new TlsrptReportPolicy
        {
            PolicyType = "sts", PolicyDomain = "bcdr.co.uk", SuccessfulSessionCount = 3, FailedSessionCount = 1,
            FailureDetails = [new TlsrptFailureDetail { ResultType = "certificate-expired", FailedSessionCount = 1, ReceivingMxHostname = "mx.example.net" }],
        });

        var detail = TlsrptReportDetail.From(report);

        Assert.False(detail.FromRawJson);
        var policy = Assert.Single(detail.Policies);
        Assert.Null(policy.Settings);
        Assert.Equal("at mx.example.net", Assert.Single(policy.Failures).Where);
        Assert.Equal("not json", TlsrptReportDetail.Pretty("not json"));
    }

    [Theory]
    [InlineData("2026-11-15T00:00:00Z", "2026-11-15T23:59:59Z", "15/11/2026")]
    [InlineData("2026-11-15T00:00:00Z", "2026-11-16T00:00:00Z", "15/11/2026")]
    [InlineData("2026-11-14T00:00:00Z", "2026-11-16T00:00:00Z", "14/11/2026 – 15/11/2026")]
    public void ADayIsOneDate(string begin, string end, string expected) =>
        Assert.Equal(expected, TlsrptReportDetail.DateRange(DateTimeOffset.Parse(begin), DateTimeOffset.Parse(end)));

    [Theory]
    [InlineData("sts", "MTA-STS")]
    [InlineData("no-policy-found", "No policy")]
    [InlineData("tlsa", "DANE")]
    [InlineData("something-new", "something-new")]
    public void PolicyTypes_HaveReadableNames(string type, string label) =>
        Assert.Equal(label, TlsrptReportDetail.PolicyTypeLabel(type));

    private static TlsrptReport Report(string json) => new()
    {
        ReportingOrg = "Google Inc.", ReportId = "r1", RawJson = json,
        DateRangeBeginUtc = DateTimeOffset.Parse("2026-11-15T00:00:00Z"), DateRangeEndUtc = DateTimeOffset.Parse("2026-11-15T23:59:59Z"),
    };
}
