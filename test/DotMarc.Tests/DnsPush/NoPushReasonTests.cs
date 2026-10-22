using DotMarc.Data;
using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class NoPushReasonTests
{
    [Fact]
    public void A_provider_dotMARC_cant_push_to_says_so_and_names_it() =>
        Assert.Equal("contoso.com's DNS is at GoDaddy, and dotMARC can only push to Cloudflare, Azure DNS and Google Cloud DNS. Use Show record and add the record at GoDaddy by hand.",
            DetectedDnsProvider.GoDaddy.NoPushReason("contoso.com"));

    [Fact]
    public void A_supported_provider_that_isnt_connected_points_to_DNS_push_settings() =>
        Assert.Contains("isn't connected to dotMARC yet. Connect it under Manage > DNS push", DetectedDnsProvider.Cloudflare.NoPushReason("contoso.com"));

    [Theory]
    [InlineData(DetectedDnsProvider.NotChecked, "hasn't been checked yet")]
    [InlineData(DetectedDnsProvider.Unknown, "doesn't recognise who hosts")]
    public void Unchecked_and_unrecognised_providers_say_which(DetectedDnsProvider provider, string expected) =>
        Assert.Contains(expected, provider.NoPushReason("contoso.com"));
}
