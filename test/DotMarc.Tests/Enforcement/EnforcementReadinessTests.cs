using DotMarc.Data;
using DotMarc.Enforcement;
using DotMarc.Senders;
using Xunit;

namespace DotMarc.Tests.Enforcement;

public sealed class EnforcementReadinessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly SenderIdentity Microsoft = new("service:microsoft", "Microsoft 365", true);
    private static readonly SenderIdentity Mailchimp = new("service:mailchimp", "Mailchimp", true);
    private static readonly SenderIdentity Spoofer = new("ip:203.0.113.9", "203.0.113.9", false);

    private static Domain Domain(DmarcPolicyLevel? policy = DmarcPolicyLevel.None, int? percent = null, int ageDays = 20) => new()
    {
        Id = 1,
        Name = "contoso.com",
        DmarcPolicy = policy,
        DmarcPercent = percent,
        FirstSeenUtc = Now.AddDays(-ageDays),
    };

    private static EnforcementAssessment Assess(Domain domain, IReadOnlyList<SenderVolume> senders, Dictionary<string, SenderApprovalStatus>? approvals = null,
        int daysOfData = 20) =>
        EnforcementReadiness.Assess(domain, senders, approvals ?? [], Now.AddDays(-daysOfData), Now);

    [Theory]
    [InlineData(null, null, EnforcementStage.NoRecord)]
    [InlineData(DmarcPolicyLevel.None, null, EnforcementStage.Monitoring)]
    [InlineData(DmarcPolicyLevel.Quarantine, 50, EnforcementStage.PartialQuarantine)]
    [InlineData(DmarcPolicyLevel.Quarantine, 100, EnforcementStage.Quarantine)]
    [InlineData(DmarcPolicyLevel.Reject, 10, EnforcementStage.PartialReject)]
    [InlineData(DmarcPolicyLevel.Reject, null, EnforcementStage.Reject)]
    public void StageOf_ReadsPolicyAndPercent(DmarcPolicyLevel? policy, int? percent, EnforcementStage expected) =>
        Assert.Equal(expected, EnforcementReadiness.StageOf(Domain(policy, percent)));

    [Fact]
    public void Assess_IsReadyWhenEverySenderIsReviewedAndApprovedOnesPass()
    {
        var result = Assess(Domain(),
            [new(Microsoft, 1000, 995), new(Spoofer, 300, 0)],
            new() { [Microsoft.Key] = SenderApprovalStatus.Approved, [Spoofer.Key] = SenderApprovalStatus.NotOurs });

        Assert.True(result.Ready);
        Assert.Equal(DmarcPolicyLevel.Quarantine, result.NextPolicy);
        Assert.Empty(result.Blockers);
    }

    [Fact]
    public void Assess_WaitsForTwoWeeksOfReports()
    {
        var result = Assess(Domain(), [new(Microsoft, 1000, 1000)], new() { [Microsoft.Key] = SenderApprovalStatus.Approved }, daysOfData: 5);

        Assert.False(result.Ready);
        Assert.Equal("Not enough reports yet", Assert.Single(result.Blockers).Title);
    }

    [Fact]
    public void Assess_BlocksOnUnreviewedSendersWithRealVolumeOnly()
    {
        var result = Assess(Domain(),
            [new(Microsoft, 10_000, 10_000), new(Mailchimp, 400, 400), new(Spoofer, 3, 0)],
            new() { [Microsoft.Key] = SenderApprovalStatus.Approved });

        Assert.Equal("1 sender not reviewed", Assert.Single(result.Blockers).Title);
    }

    [Fact]
    public void Assess_BlocksOnApprovedSendersFailingDmarc()
    {
        var result = Assess(Domain(DmarcPolicyLevel.Quarantine),
            [new(Microsoft, 1000, 1000), new(Mailchimp, 400, 200)],
            new() { [Microsoft.Key] = SenderApprovalStatus.Approved, [Mailchimp.Key] = SenderApprovalStatus.Approved });

        Assert.Equal(DmarcPolicyLevel.Reject, result.NextPolicy);
        var blocker = Assert.Single(result.Blockers);
        Assert.Equal("1 approved sender failing DMARC", blocker.Title);
        Assert.Contains("rejected", blocker.Detail);
    }

    [Fact]
    public void Assess_FlagsADomainLeftOnNoneTooLong()
    {
        Assert.True(Assess(Domain(ageDays: 45), []).StuckMonitoring);
        Assert.False(Assess(Domain(ageDays: 10), []).StuckMonitoring);
        Assert.False(Assess(Domain(DmarcPolicyLevel.Quarantine, ageDays: 45), []).StuckMonitoring);
    }

    [Fact]
    public void Assess_HasNoNextStepOnceRejecting()
    {
        var result = Assess(Domain(DmarcPolicyLevel.Reject), [new(Microsoft, 1000, 500)]);

        Assert.True(result.Enforced);
        Assert.Null(result.NextPolicy);
        Assert.False(result.Ready);
        Assert.Empty(result.Blockers);
    }
}
