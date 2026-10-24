using DotMarc.Lookalikes;
using Xunit;

namespace DotMarc.Tests.Lookalikes;

public sealed class LookalikeGeneratorTests
{
    [Fact]
    public void A_two_part_ending_varies_the_name_and_keeps_the_ending()
    {
        var candidates = LookalikeGenerator.For("budgetwaste.co.uk");

        Assert.Contains(new LookalikeCandidate("budgetwast.co.uk", "Letter missing"), candidates);
        Assert.Contains(new LookalikeCandidate("budgetwaste.com", "Other ending"), candidates);
        Assert.Contains(new LookalikeCandidate("budgetvvaste.co.uk", "Look-alike characters"), candidates);
        Assert.DoesNotContain(candidates, candidate => candidate.Name == "budgetwaste.co.uk");
        Assert.DoesNotContain(candidates, candidate => candidate.Name.EndsWith(".uk.uk", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_variant_is_distinct_and_within_the_cap()
    {
        var candidates = LookalikeGenerator.For("contoso.com");

        Assert.NotEmpty(candidates);
        Assert.True(candidates.Count <= LookalikeGenerator.MaxCandidates);
        Assert.Equal(candidates.Count, candidates.Select(candidate => candidate.Name).Distinct().Count());
        Assert.Contains(new LookalikeCandidate("cnotoso.com", "Letters swapped"), candidates);
        Assert.Contains(new LookalikeCandidate("contoso.co.uk", "Other ending"), candidates);
    }

    [Theory]
    [InlineData("mail.contoso.com")]
    [InlineData("ab.com")]
    public void A_subdomain_or_very_short_name_has_none(string domain)
    {
        Assert.Empty(LookalikeGenerator.For(domain));
    }
}
