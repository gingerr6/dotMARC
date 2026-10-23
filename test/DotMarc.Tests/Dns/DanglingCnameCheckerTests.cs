using System.Net;
using System.Text;
using System.Web;
using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class DanglingCnameCheckerTests
{
    /// <summary>Answers DNS-over-HTTPS queries from a table of (name, type) to JSON body; anything else doesn't exist.</summary>
    private sealed class DnsTable : HttpMessageHandler
    {
        public Dictionary<(string Name, string Type), string> Answers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var body = Answers.GetValueOrDefault((query["name"]!, query["type"]!), """{"Status":3}""");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        public void Cname(string name, string target) => Answers[(name, "CNAME")] = $$"""{"Status":0,"Answer":[{"type":5,"data":"{{target}}."}]}""";

        public void Exists(string name) => Answers[(name, "A")] = """{"Status":0,"Answer":[{"type":1,"data":"192.0.2.1"}]}""";
    }

    private static (DanglingCnameChecker Checker, DnsTable Dns) Create()
    {
        var dns = new DnsTable();
        return (new DanglingCnameChecker(new HttpClient(dns) { BaseAddress = new Uri("https://cloudflare-dns.com/") }), dns);
    }

    [Fact]
    public async Task A_cname_to_a_name_that_no_longer_exists_is_a_takeover_risk()
    {
        var (checker, dns) = Create();
        dns.Cname("shop.contoso.com", "contoso-shop.azurewebsites.net");

        var result = await checker.CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(DanglingCnameStatus.Dangling, result.Status);
        Assert.Contains("shop.contoso.com → contoso-shop.azurewebsites.net", result.Detail);
    }

    [Fact]
    public async Task Cnames_whose_targets_exist_are_fine()
    {
        var (checker, dns) = Create();
        dns.Cname("autodiscover.contoso.com", "autodiscover.outlook.com");
        dns.Exists("autodiscover.outlook.com");

        var result = await checker.CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(DanglingCnameStatus.Ok, result.Status);
        Assert.Contains("1 CNAME", result.Detail);
    }

    [Fact]
    public async Task The_chain_is_followed_to_its_last_name()
    {
        var (checker, dns) = Create();
        dns.Cname("www.contoso.com", "contoso.cdn.example");
        dns.Cname("contoso.cdn.example", "gone.edge.example");

        var result = await checker.CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(DanglingCnameStatus.Dangling, result.Status);
        Assert.Contains("www.contoso.com → gone.edge.example", result.Detail);
    }

    [Fact]
    public async Task A_cname_loop_ends_rather_than_spinning()
    {
        var (checker, dns) = Create();
        dns.Cname("www.contoso.com", "a.example");
        dns.Cname("a.example", "b.example");
        dns.Cname("b.example", "a.example");
        dns.Exists("b.example");

        var result = await checker.CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(DanglingCnameStatus.Ok, result.Status);
    }

    [Fact]
    public async Task No_cnames_at_all_is_fine()
    {
        var (checker, _) = Create();

        var result = await checker.CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(DanglingCnameStatus.Ok, result.Status);
        Assert.Contains("No CNAME records", result.Detail);
    }
}
