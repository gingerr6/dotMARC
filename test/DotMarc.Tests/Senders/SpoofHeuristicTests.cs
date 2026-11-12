using DotMarc.Data;
using DotMarc.Senders;
using Xunit;

namespace DotMarc.Tests.Senders;

public sealed class SpoofHeuristicTests
{
    private static SpoofEvidence Evidence(int volume = 4, bool pass = false, string? host = null, bool resolved = true, bool listed = false, string ip = "203.0.113.9") =>
        new(ip, volume, pass, host, resolved, listed);

    [Fact]
    public void PassingAlignedMail_IsNeverFlagged()
    {
        Assert.False(SpoofHeuristic.Assess(Evidence(pass: true, host: null, resolved: true)).IsLikelySpoof);
        Assert.False(SpoofHeuristic.Assess(Evidence(pass: true, host: "203-0-113-9.hsd1.example.net")).IsLikelySpoof);
        Assert.False(SpoofHeuristic.Assess(Evidence(pass: true, listed: true, host: null)).IsLikelySpoof);
    }

    [Fact]
    public void ASmallFailure_WithNoReverseDns_IsFlagged()
    {
        var result = SpoofHeuristic.Assess(Evidence(host: null, resolved: true));

        Assert.True(result.IsLikelySpoof);
        Assert.Contains("no reverse DNS", result.Reason);
        Assert.Contains("both SPF and DKIM failed", result.Reason);
    }

    [Fact]
    public void AMissingLookup_IsNotTreatedAsNoReverseDns()
    {
        Assert.False(SpoofHeuristic.Assess(Evidence(host: null, resolved: false)).IsLikelySpoof);
    }

    [Theory]
    [InlineData("203-0-113-9.hsd1.example.net", "203.0.113.9")]
    [InlineData("9-113-0-203.isp.example.net", "203.0.113.9")]
    [InlineData("customer.dyn.rr.com", "198.51.100.4")]
    [InlineData("pool-12.isp.example.net", "198.51.100.4")]
    [InlineData("user.dsl.example.net", "198.51.100.4")]
    public void ResidentialOrDynamicNames_AreFlagged(string host, string ip)
    {
        var result = SpoofHeuristic.Assess(Evidence(host: host, ip: ip));

        Assert.True(result.IsLikelySpoof);
        Assert.Contains("residential or dynamic", result.Reason);
    }

    [Theory]
    [InlineData("mail.contoso.com")]
    [InlineData("mail.dyn.com")]
    [InlineData("mailpool.example.com")]
    public void ANamedMailServer_ThatFails_IsLeftForReview(string host)
    {
        Assert.False(SpoofHeuristic.Assess(Evidence(host: host)).IsLikelySpoof);
    }

    [Fact]
    public void ABlocklistHit_DoesNotFlagANamedMailServer()
    {
        Assert.False(SpoofHeuristic.Assess(Evidence(host: "mail.contoso.com", listed: true)).IsLikelySpoof);
    }

    [Fact]
    public void ABlocklistHit_IsMentioned_WhenTheAddressAlreadyLooksUnprovisioned()
    {
        var result = SpoofHeuristic.Assess(Evidence(host: null, resolved: true, listed: true));

        Assert.True(result.IsLikelySpoof);
        Assert.Contains("blocklist", result.Reason);
    }

    [Fact]
    public void DetailMentions_TheAddress_AndNotALongerOne()
    {
        const string detail = "Listed: 203.0.113.9 (in SPF) on Spamhaus ZEN; 203.0.113.90 (mail server) on SpamCop.";

        Assert.True(SpoofHeuristic.DetailMentionsIp(detail, "203.0.113.9"));
        Assert.True(SpoofHeuristic.DetailMentionsIp(detail, "203.0.113.90"));
        Assert.False(SpoofHeuristic.DetailMentionsIp(detail, "203.0.113.91"));
    }

    [Fact]
    public void MoreThanFiftyMessages_IsLeftForAPerson()
    {
        Assert.False(SpoofHeuristic.Assess(Evidence(volume: 51, host: null, resolved: true)).IsLikelySpoof);
        Assert.True(SpoofHeuristic.Assess(Evidence(volume: 50, host: null, resolved: true)).IsLikelySpoof);
    }

    [Fact]
    public void DetectedForwarding_WinsOverTheSpoofFlag()
    {
        var spoof = SpoofHeuristic.Assess(Evidence(host: null, resolved: true));
        var identity = SenderIdentity.For("203.0.113.9", null, null, "Receivers reported this mail as forwarded.", spoof);

        Assert.True(identity.IsForwarder);
        Assert.False(identity.IsLikelySpoof);
        Assert.Null(identity.SpoofReason);
    }

    [Fact]
    public void ASavedDecision_ReplacesTheSpoofLabel_AndDropsItFromTheUnreviewedCount()
    {
        var identity = new SenderIdentity("ip:203.0.113.9", "203.0.113.9", false, IsLikelySpoof: true);
        var forwarder = new SenderIdentity("cloudflare-email-routing", "Cloudflare Email Routing", true, IsForwarder: true);

        Assert.Equal(SourceReview.LikelySpoofLabel, SourceReview.Label(identity, null));
        Assert.Equal("Approved", SourceReview.Label(identity, SenderApprovalStatus.Approved));
        Assert.Equal("Not ours", SourceReview.Label(identity, SenderApprovalStatus.NotOurs));
        Assert.False(SourceReview.CountsAsUnreviewed(identity, null));
        Assert.False(SourceReview.CountsAsUnreviewed(identity, SenderApprovalStatus.Approved));
        Assert.False(SourceReview.CountsAsUnreviewed(forwarder, null));
        Assert.True(SourceReview.CountsAsUnreviewed(new SenderIdentity("ip:192.0.2.1", "192.0.2.1", false), null));
    }

    [Fact]
    public void Ipv6_GenericPointers_NeedSeveralRealHextets()
    {
        Assert.True(SpoofHeuristic.LooksDynamicOrResidential("2001-db8-85a3-8d3.ip6.example.net", "2001:db8:85a3:8d3::1"));
        Assert.False(SpoofHeuristic.LooksDynamicOrResidential("mail-0-0-0-0.example.net", "2001:db8::1"));
    }
}
