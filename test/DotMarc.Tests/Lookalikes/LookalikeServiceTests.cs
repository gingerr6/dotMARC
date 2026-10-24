using DotMarc.Data;
using DotMarc.Lookalikes;
using Xunit;

namespace DotMarc.Tests.Lookalikes;

public sealed class LookalikeServiceTests
{
    [Fact]
    public void A_lookalike_that_takes_mail_is_found()
    {
        var domain = new Domain { Name = "contoso.com" };

        LookalikeService.ApplyStatus(domain, [("contos0.com", true, false), ("contoso.net", false, false)], 200);

        Assert.Equal(LookalikeStatus.Found, domain.LookalikeStatus);
        Assert.Contains("contos0.com", domain.LookalikeDetail);
        Assert.DoesNotContain("contoso.net", domain.LookalikeDetail);
    }

    [Fact]
    public void Dismissed_and_mail_less_lookalikes_leave_it_clear()
    {
        var domain = new Domain { Name = "contoso.com" };

        LookalikeService.ApplyStatus(domain, [("contos0.com", true, true), ("contoso.net", false, false)], 200);

        Assert.Equal(LookalikeStatus.Clear, domain.LookalikeStatus);
        Assert.Equal("2 of the 200 variants checked are registered, but none that can receive mail and hasn't been dismissed.", domain.LookalikeDetail);
    }

    [Fact]
    public void None_registered_is_clear()
    {
        var domain = new Domain { Name = "contoso.com" };

        LookalikeService.ApplyStatus(domain, [], 200);

        Assert.Equal((LookalikeStatus.Clear, "None of the 200 typo and look-alike variants checked is registered."), (domain.LookalikeStatus, domain.LookalikeDetail));
    }
}
