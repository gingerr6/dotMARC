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
}
