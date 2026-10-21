using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class ReturnPathDomainsTests
{
    private static ReportRecord Record(string ip, int count, params (string Domain, DmarcMechanismResult Result, string? Scope)[] spf) => new()
    {
        SourceIp = ip,
        MessageCount = count,
        HeaderFrom = "mspcyber.uk",
        AuthDetails = spf.Select(check => new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Spf, Domain = check.Domain, Result = check.Result, Scope = check.Scope }).ToList(),
    };

    [Fact]
    public void From_ListsSubdomainsThatSpfWasCheckedOn_BusiestFirst()
    {
        var report = new Report { ReportingOrg = "google.com", ReportId = "1", RawXml = "<x/>" };
        report.Records.Add(Record("203.0.113.1", 120, ("EM1234.mspcyber.uk.", DmarcMechanismResult.Pass, "MFrom")));
        report.Records.Add(Record("203.0.113.2", 30, ("em1234.mspcyber.uk", DmarcMechanismResult.Fail, null)));
        report.Records.Add(Record("203.0.113.3", 500, ("bounce.mspcyber.uk", DmarcMechanismResult.Pass, "MFrom")));
        report.Records.Add(Record("203.0.113.4", 900, ("mspcyber.uk", DmarcMechanismResult.Pass, "MFrom")));
        report.Records.Add(Record("203.0.113.5", 70, ("mail.mspcyber.uk", DmarcMechanismResult.Pass, "Helo")));
        report.Records.Add(Record("203.0.113.6", 40, ("notmspcyber.uk", DmarcMechanismResult.Pass, "MFrom")));

        var domains = ReturnPathDomains.From("mspcyber.uk", [report]);

        Assert.Equal(["bounce.mspcyber.uk", "em1234.mspcyber.uk"], domains.Select(domain => domain.Name));
        var smtp2go = domains[1];
        Assert.Equal((150, 120, 0.8), (smtp2go.Volume, smtp2go.Passing, smtp2go.PassRate));
        Assert.Equal(["203.0.113.1", "203.0.113.2"], smtp2go.SourceIps);
    }
}
