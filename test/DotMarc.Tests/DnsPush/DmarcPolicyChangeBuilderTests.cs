using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DmarcPolicyChangeBuilderTests
{
    private static Task<DnsChangePlan> BuildAsync(FakeDmarcTxtLookup lookup, string? payload) =>
        new DmarcPolicyChangeBuilder(lookup).BuildAsync(
            new DnsPushRequest(new Domain { Id = 1, Name = "contoso.com" }, "cloudflare", "contoso.com", payload), CancellationToken.None);

    [Theory]
    [InlineData("v=DMARC1; p=none; rua=mailto:a@b.example", DmarcPolicyLevel.Quarantine, "v=DMARC1; p=quarantine; rua=mailto:a@b.example")]
    [InlineData("v=DMARC1; p=quarantine; pct=25; rua=mailto:a@b.example; sp=none", DmarcPolicyLevel.Reject, "v=DMARC1; p=reject; rua=mailto:a@b.example; sp=none")]
    [InlineData("v=DMARC1;rua=mailto:a@b.example", DmarcPolicyLevel.Quarantine, "v=DMARC1; p=quarantine; rua=mailto:a@b.example")]
    public void WithPolicy_SetsPolicyDropsPctAndKeepsTheRest(string record, DmarcPolicyLevel policy, string expected) =>
        Assert.Equal(expected, DmarcPolicyChangeBuilder.WithPolicy(record, policy));

    [Fact]
    public async Task DmarcPolicyChangeBuilder_MergesTheNewPolicyIntoTheLiveRecord()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=none; rua=mailto:a@b.example", null) };

        var change = Assert.Single((await BuildAsync(lookup, "reject")).Changes);

        Assert.Equal((DnsRecordChangeKind.Merge, "_dmarc.contoso.com", "v=DMARC1; p=reject; rua=mailto:a@b.example", "v=DMARC1; p=none; rua=mailto:a@b.example"),
            (change.Kind, change.Name, change.DesiredValue, change.ExistingValue));
    }

    [Fact]
    public async Task DmarcPolicyChangeBuilder_ChangesNothingWhenThePolicyIsAlreadySet()
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=reject", null) };

        Assert.Empty((await BuildAsync(lookup, "reject")).Changes);
    }

    [Theory]
    [InlineData("none")]
    [InlineData(null)]
    public async Task DmarcPolicyChangeBuilder_RefusesAPolicyThatIsntAStepUp(string? payload)
    {
        var lookup = new FakeDmarcTxtLookup { Result = new("v=DMARC1; p=none", null) };

        Assert.Equal("error", (await BuildAsync(lookup, payload)).Refusal);
    }

    [Fact]
    public async Task DmarcPolicyChangeBuilder_RefusesAMissingOrDelegatedRecord()
    {
        Assert.Equal("error", (await BuildAsync(new FakeDmarcTxtLookup(), "quarantine")).Refusal);
        Assert.Equal("error", (await BuildAsync(new FakeDmarcTxtLookup { Result = new(null, "contoso.dmarc.example") }, "quarantine")).Refusal);
    }
}
