using System.Net;
using System.Text;
using DotMarc.Lookalikes;
using Xunit;

namespace DotMarc.Tests.Lookalikes;

public sealed class LookalikeCheckerTests
{
    /// <summary>Answers DNS-over-HTTPS queries from a table of name/type to answer JSON.</summary>
    private sealed class DohHandler : HttpMessageHandler
    {
        public Dictionary<(string Name, string Type), string> Answers { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var body = Answers.TryGetValue((query["name"]!, query["type"]!), out var answer) ? $$"""{"Status":0,"Answer":[{{answer}}]}""" : """{"Status":3}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/dns-json") });
        }
    }

    [Fact]
    public async Task It_reports_registered_names_and_whether_they_take_mail()
    {
        var handler = new DohHandler();
        handler.Answers[("contos0.com", "NS")] = """{"name":"contos0.com","type":2,"data":"ns1.example.net."}""";
        handler.Answers[("contos0.com", "MX")] = """{"name":"contos0.com","type":15,"data":"10 mx.example.net."}""";
        handler.Answers[("contoso.net", "NS")] = """{"name":"contoso.net","type":2,"data":"ns1.example.net."}""";
        handler.Answers[("contoso.net", "MX")] = """{"name":"contoso.net","type":15,"data":"0 ."}""";
        handler.Answers[("contoso.org", "NS")] = """{"name":"contoso.org","type":5,"data":"elsewhere.example."}""";
        var checker = new LookalikeChecker(new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") });

        var found = await checker.FindRegisteredAsync(
            [new("contos0.com", "Look-alike characters"), new("contoso.net", "Other ending"), new("contoso.org", "Other ending"), new("contosoo.com", "Letter doubled")],
            CancellationToken.None);

        Assert.Equal([new RegisteredLookalike("contos0.com", "Look-alike characters", true), new RegisteredLookalike("contoso.net", "Other ending", false)], found);
    }
}
