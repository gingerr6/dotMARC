using System.Net;
using DotMarc.IpEnrichment;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.IpEnrichment;

public sealed class ReverseDnsLookupTests
{
    private static (ReverseDnsLookup Lookup, FakeHttpMessageHandler Handler) Create()
    {
        var handler = new FakeHttpMessageHandler();
        return (new ReverseDnsLookup(new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") }), handler);
    }

    [Fact]
    public void ReverseName_ReversesIpv4Octets()
    {
        Assert.Equal("85.22.107.40.in-addr.arpa", ReverseDnsLookup.ReverseName(IPAddress.Parse("40.107.22.85")));
    }

    [Fact]
    public void ReverseName_ReversesEveryIpv6Nibble()
    {
        Assert.Equal("1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa",
            ReverseDnsLookup.ReverseName(IPAddress.Parse("2001:db8::1")));
    }

    [Fact]
    public async Task LookupAsync_ReturnsTheName_WhenItResolvesBackToTheIp()
    {
        var (lookup, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":12,"data":"Mail-AM6EUR05ON2101.outbound.protection.outlook.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":28,"data":"2a01:111:f400:7e1b::101"}]}""");

        var name = await lookup.LookupAsync("2a01:111:f400:7e1b::101", CancellationToken.None);

        Assert.Equal("mail-am6eur05on2101.outbound.protection.outlook.com", name);
        Assert.Contains("type=PTR", handler.Requests[0].RequestUri!.Query);
        Assert.Contains("type=AAAA", handler.Requests[1].RequestUri!.Query);
    }

    [Fact]
    public async Task LookupAsync_IgnoresAName_ThatDoesntResolveBackToTheIp()
    {
        // Anyone controlling an IP's reverse zone can claim any name; the forward zone decides.
        var (lookup, handler) = Create();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":12,"data":"mail.google.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":1,"data":"142.250.0.1"}]}""");

        Assert.Null(await lookup.LookupAsync("203.0.113.9", CancellationToken.None));
    }

    [Fact]
    public async Task LookupAsync_IsNull_WhenThereIsNoPtrRecord()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """{"Status":3}""";

        Assert.Null(await lookup.LookupAsync("203.0.113.9", CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LookupAsync_Throws_WhenTheResolverCantAnswer()
    {
        var (lookup, handler) = Create();
        handler.ResponseBody = """{"Status":2}""";

        await Assert.ThrowsAsync<HttpRequestException>(() => lookup.LookupAsync("203.0.113.9", CancellationToken.None));
    }
}
