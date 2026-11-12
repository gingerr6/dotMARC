using DotMarc.Senders;
using Xunit;

namespace DotMarc.Tests.Senders;

public sealed class KnownSendersTests
{
    [Theory]
    [InlineData("mail-db8eur05on2101.outbound.protection.outlook.com", "microsoft365")]
    [InlineData("MAIL-SOR-F41.GOOGLE.COM.", "google")]
    [InlineData("me-in-f20.1e100.net", "google")]
    [InlineData("a8-31.smtp-out.amazonses.com", "amazonses")]
    [InlineData("mail181.atl121.mcsv.net", "mailchimp")]
    [InlineData("o1.ptr1234.sendgrid.net", "sendgrid")]
    [InlineData("mx0a-00123.pphosted.com", "proofpoint")]
    public void Recognise_KnowsAServiceByItsHostName(string hostName, string expectedKey)
    {
        Assert.Equal(expectedKey, KnownSenders.Recognise(hostName, organization: null)?.Key);
    }

    [Fact]
    public void Recognise_DoesNotMatchAHostThatOnlyEndsWithTheSameLetters()
    {
        Assert.Null(KnownSenders.Recognise("mail.notgoogle.com", organization: null));
    }

    [Fact]
    public void Recognise_FallsBackToTheOwner_ForMailOnlyCompanies()
    {
        Assert.Equal("mimecast", KnownSenders.Recognise(hostName: null, "Mimecast North America Inc")?.Key);
    }

    [Fact]
    public void Recognise_IgnoresACloudProvidersOwnerName_SinceItCoversEveryCustomersServers()
    {
        Assert.Null(KnownSenders.Recognise(hostName: null, "Microsoft Corporation"));
        Assert.Null(KnownSenders.Recognise(hostName: null, "Google LLC"));
    }

    [Fact]
    public void SenderIdentity_IsTheServiceWhenRecognised_ElseTheIp()
    {
        var service = SenderIdentity.For("40.107.1.1", "mail-x.outbound.protection.outlook.com", null);
        var server = SenderIdentity.For("203.0.113.5", "mail.contoso.com", null);

        Assert.Equal(("microsoft365", "Microsoft 365", true), (service.Key, service.Label, service.IsService));
        Assert.Equal(("ip:203.0.113.5", "203.0.113.5", false), (server.Key, server.Label, server.IsService));
        Assert.Equal("203.0.113.5", SenderIdentity.LabelFor(server.Key));
        Assert.Equal("Microsoft 365", SenderIdentity.LabelFor(service.Key));
    }

    [Fact]
    public void Keys_AreUnique()
    {
        Assert.Equal(KnownSenders.All.Count, KnownSenders.All.Select(sender => sender.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("mail213.smtp2go.com")]
    [InlineData("return.smtp2go.net")]
    public void Recognise_Smtp2Go(string hostName) =>
        Assert.Equal("SMTP2GO", KnownSenders.Recognise(hostName, null)?.Name);

    [Fact]
    public void Recognise_KnowsCloudflareEmailRouting_AsAForwarder()
    {
        var identity = SenderIdentity.For("104.30.8.177", "i-bhh.cloudflare-email.net", "Cloudflare, Inc.");

        Assert.Equal(("cloudflare-email-routing", "Cloudflare Email Routing", true, true), (identity.Key, identity.Label, identity.IsService, identity.IsForwarder));
    }

    [Fact]
    public void Recognise_DoesNotCallASendingServiceAForwarder()
    {
        Assert.False(SenderIdentity.For("192.0.2.1", "mail-db8eur05on2101.outbound.protection.outlook.com", null).IsForwarder);
    }

    [Fact]
    public void GoogleWorkspace_StaysASender_AndGoogleLlcIsNotAForwarder()
    {
        var workspace = SenderIdentity.For("192.0.2.1", "me-in-f20.1e100.net", "Google LLC");
        var googleCom = SenderIdentity.For("192.0.2.2", "mail.google.com", "Google LLC");
        var ownerOnly = SenderIdentity.For("8.8.8.8", hostName: null, "Google LLC");

        Assert.Equal(("google", false, false), (workspace.Key, workspace.IsForwarder, workspace.IsLikelySpoof));
        Assert.Equal(("google", false), (googleCom.Key, googleCom.IsForwarder));
        Assert.False(ownerOnly.IsService);
        Assert.False(ownerOnly.IsForwarder);
        Assert.Null(KnownSenders.Recognise(hostName: null, "Google LLC"));
    }

    [Theory]
    [InlineData("x0b-00.v6.unverified-forwarding.1e100.net")]
    [InlineData("X0B-00.V6.UNVERIFIED-FORWARDING.1E100.NET.")]
    public void UnverifiedForwarding_IsItsOwnForwarder_EvenThoughTheShorterGoogleSuffixIsListedFirst(string hostName)
    {
        var google = IndexOf("google");
        var forwarding = IndexOf("google-unverified-forwarding");
        Assert.True(forwarding > google, "The longer suffix is listed later, so first-match would return Google Workspace.");

        var identity = SenderIdentity.For("2600:1901:101::10", hostName, "Google LLC");

        Assert.Equal(("google-unverified-forwarding", "Google unverified forwarding", true, true),
            (identity.Key, identity.Label, identity.IsService, identity.IsForwarder));
        Assert.NotEqual("google", identity.Key);
    }

    [Fact]
    public void AHostThatOnlyEndsInTheSameLetters_DoesNotMatchTheForwardingSuffix()
    {
        Assert.Equal("google", KnownSenders.Recognise("notunverified-forwarding.1e100.net", null)?.Key);
    }

    [Theory]
    [InlineData("108.177.16.1")]
    [InlineData("108.177.17.255")]
    [InlineData("142.250.220.10")]
    [InlineData("142.250.221.1")]
    [InlineData("2600:1901:101::1")]
    [InlineData("2600:1901:101:ffff::1")]
    [InlineData("::ffff:108.177.16.5")]
    public void UnverifiedForwarding_MatchesPublishedRanges_WhenThereIsNoHostname(string ip)
    {
        var identity = SenderIdentity.For(ip, hostName: null, organization: "Google LLC");

        Assert.Equal("google-unverified-forwarding", identity.Key);
        Assert.True(identity.IsForwarder);
    }

    [Theory]
    [InlineData("108.177.15.255")]
    [InlineData("108.177.18.0")]
    [InlineData("142.250.219.255")]
    [InlineData("142.250.222.0")]
    [InlineData("2600:1901:102::1")]
    [InlineData("8.8.8.8")]
    public void AddressesOutsideTheForwardingRanges_AreNotThatForwarder(string ip)
    {
        Assert.Null(KnownSenders.Recognise(hostName: null, organization: null, ip));
    }

    [Fact]
    public void AConfirmedHostname_WinsOverAForwardingRange()
    {
        Assert.Equal("google", KnownSenders.Recognise("me-in-f20.1e100.net", "Google LLC", "108.177.16.1")?.Key);
        Assert.Null(KnownSenders.Recognise("mail.contoso.com", "Google LLC", "108.177.16.1"));
        Assert.Null(KnownSenders.Recognise("mail.contoso.com", null, "2600:1901:101::20"));
    }

    [Fact]
    public void GroupsIo_IsAForwarder_ByHostnameAndByPublishedAddress()
    {
        Assert.True(SenderIdentity.For("66.175.222.108", "mail02.groups.io", null).IsForwarder);
        Assert.Equal("groups-io", SenderIdentity.For("66.175.222.108", hostName: null, organization: null).Key);
        Assert.Equal("groups-io", KnownSenders.Recognise(null, null, "45.79.224.7")?.Key);
        Assert.Null(KnownSenders.Recognise("mail.contoso.com", null, "66.175.222.108"));
    }

    [Fact]
    public void CloudflareEmailRouting_MatchesItsPublishedRanges_WhenTheNameIsMissing()
    {
        Assert.Equal("cloudflare-email-routing", KnownSenders.Recognise(null, null, "104.30.8.177")?.Key);
        Assert.Equal("cloudflare-email-routing", KnownSenders.Recognise(null, null, "2405:8100:c000::1")?.Key);
        Assert.Equal("cloudflare-email-routing", KnownSenders.Recognise("a-h.cloudflare-email.org", null)?.Key);
    }

    [Fact]
    public void PublishedRanges_AllParse_AndContainTheirOwnNetworkAddress()
    {
        foreach (var sender in KnownSenders.All)
        {
            foreach (var cidr in sender.Cidrs ?? [])
            {
                Assert.True(CidrMatcher.TryParse(cidr, out var prefix), cidr);
                Assert.True(CidrMatcher.Contains(cidr, prefix.Network, out var length), cidr);
                Assert.Equal(prefix.Length, length);
            }
        }
    }

    [Fact]
    public void ARecognisedService_IgnoresASpoofAssessment()
    {
        var spoof = new SpoofAssessment(true, "should be ignored");

        var identity = SenderIdentity.For("192.0.2.1", "me-in-f20.1e100.net", "Google LLC", detectedForwarding: null, spoof);

        Assert.False(identity.IsLikelySpoof);
        Assert.Null(identity.SpoofReason);
        Assert.False(identity.IsForwarder);
    }

    private static int IndexOf(string key)
    {
        var index = 0;
        foreach (var sender in KnownSenders.All)
        {
            if (sender.Key == key)
            {
                return index;
            }

            index++;
        }

        return -1;
    }
}
