using DotMarc.Data;
using DotMarc.Scoring;
using Xunit;

namespace DotMarc.Tests.Scoring;

public sealed class SecurityScoreTests
{
    private static Domain FullyProtected() => new()
    {
        Name = "contoso.com",
        DmarcCheckStatus = DmarcCheckStatus.Ok,
        DmarcPolicy = DmarcPolicyLevel.Reject,
        SpfCheckStatus = SpfCheckStatus.Ok,
        DkimCheckStatus = DkimCheckStatus.Ok,
        MtaStsEnabled = true,
        MtaStsStatus = MtaStsStatus.Active,
        MtaStsMode = MtaStsMode.Enforce,
        TlsrptCheckStatus = TlsrptCheckStatus.Ok,
    };

    private static readonly SecurityScore.ReportRates Clean = new(0.995, 0.99);

    [Fact]
    public void For_GivesFullMarksToAFullyProtectedDomain()
    {
        var score = SecurityScore.For(FullyProtected(), Clean);

        Assert.Equal((100, "A"), (score.Score, score.Grade));
        Assert.Empty(score.Improvements);
    }

    [Fact]
    public void For_ScoresAMonitoringOnlyDomainLow_AndPutsEnforcementFirst()
    {
        var domain = FullyProtected();
        domain.DmarcPolicy = DmarcPolicyLevel.None;
        domain.MtaStsMode = MtaStsMode.Testing;

        var score = SecurityScore.For(domain, Clean);

        Assert.Equal((65, "C"), (score.Score, score.Grade));
        Assert.Equal("DMARC enforcement", score.Improvements.First().Name);
    }

    [Fact]
    public void For_ScoresANewDomainWithNothingSetUpAsF()
    {
        var score = SecurityScore.For(new Domain { Name = "new.example" }, new(null, null));

        Assert.Equal((0, "F"), (score.Score, score.Grade));
    }

    [Fact]
    public void For_CreditsDkimSeenPassingInReportsWhenSelectorsArentSetUp()
    {
        var domain = FullyProtected();
        domain.DkimCheckStatus = DkimCheckStatus.NotConfigured;

        Assert.Equal(12, SecurityScore.For(domain, Clean).Factors.Single(factor => factor.Name == "DKIM signing").Points);
        Assert.Equal(0, SecurityScore.For(domain, new(0.99, 0.2)).Factors.Single(factor => factor.Name == "DKIM signing").Points);
    }

    [Fact]
    public void For_LeavesOutMailSecurityForAParkedDomain()
    {
        var parked = new Domain { Name = "parked.example", DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.Reject, SpfCheckStatus = SpfCheckStatus.NullSpf };

        var score = SecurityScore.For(parked, new(null, null));

        Assert.Equal(100, score.Score);
        Assert.DoesNotContain(score.Factors, factor => factor.Name is "MTA-STS" or "DKIM signing" or "Mail passing DMARC");
    }

    [Theory]
    [InlineData(90, "A")]
    [InlineData(89, "B")]
    [InlineData(75, "B")]
    [InlineData(60, "C")]
    [InlineData(40, "D")]
    [InlineData(39, "F")]
    public void GradeOf_UsesTheBands(int score, string grade) => Assert.Equal(grade, SecurityScore.GradeOf(score));

    [Fact]
    public void Average_IsTheMeanOfTheDomainsOrNullWithNone()
    {
        Assert.Null(SecurityScore.Average([]));
        Assert.Equal(83, SecurityScore.Average([new DomainScore(100, "A", []), new DomainScore(65, "C", []), new DomainScore(84, "B", [])]));
    }

    [Fact]
    public void For_LeavesOutDomainChecksThatHaventRun()
    {
        var score = SecurityScore.For(FullyProtected(), Clean);

        Assert.DoesNotContain(score.Factors, factor => factor.Name is "Domain registration" or "No takeover risks" or "Website certificates");
    }

    [Fact]
    public void For_CountsATakeoverRiskAndAnExpiringRegistration()
    {
        var domain = FullyProtected();
        domain.DanglingCnameStatus = DanglingCnameStatus.Dangling;
        domain.DomainExpiryStatus = DomainExpiryStatus.ExpiringSoon;
        domain.CertificateStatus = CertificateCheckStatus.Ok;

        var score = SecurityScore.For(domain, Clean);

        Assert.True(score.Score < 100);
        Assert.Equal("No takeover risks", score.Improvements.First().Name);
        Assert.Contains(score.Factors, factor => factor is { Name: "Website certificates", Full: true });
    }
}
