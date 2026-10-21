using System.Net;
using System.Text;
using DotMarc.Audit;
using DotMarc.Bimi;
using DotMarc.Data;
using DotMarc.DnsPush;
using DotMarc.MtaSts;
using DotMarc.Tests.Api;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace DotMarc.Tests.Bimi;

[Collection("Postgres")]
public sealed class BimiHostingTests(PostgresContainerFixture fixture)
{
    private const string Host = "dotmarc.example";

    // An ordinary export from a design tool: no profile, a script, an onload handler, x/y, not square, no title.
    private const string DesignToolSvg = """
        <?xml version="1.0" encoding="UTF-8"?>
        <svg xmlns="http://www.w3.org/2000/svg" x="0" y="0" width="200" height="100" viewBox="0 0 200 100" onload="alert(1)">
          <metadata>Created with SomeTool</metadata>
          <script>alert(1)</script>
          <rect width="200" height="100" fill="#0a5"/>
        </svg>
        """;

    private static readonly AuditActor Actor = AuditActor.ForUser("oid", "jo@example.com", "Jo");

    [Fact]
    public void Prepare_MakesAnOrdinarySvgBimiReady()
    {
        var result = SvgTinyPs.Prepare(Encoding.UTF8.GetBytes(DesignToolSvg), "BCDR Solutions");

        Assert.Empty(result.Problems);
        Assert.NotNull(result.Svg);
        Assert.Empty(DotMarc.Dns.BimiChecker.LogoProblems(result.Svg));
        var svg = Encoding.UTF8.GetString(result.Svg);
        Assert.Contains("<title>BCDR Solutions</title>", svg);
        Assert.Contains("viewBox=\"0 -50 200 200\"", svg);
        Assert.DoesNotContain("script", svg);
        Assert.DoesNotContain("onload", svg);
    }

    [Fact]
    public void Prepare_ReportsWhatItCantFix()
    {
        var result = SvgTinyPs.Prepare(Encoding.UTF8.GetBytes("""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><image href="logo.png"/></svg>"""), "X");

        Assert.Null(result.Svg);
        Assert.Contains(result.Problems, problem => problem.Contains("embedded images"));
        Assert.Equal(["That isn't a valid SVG file."], SvgTinyPs.Prepare("not svg"u8.ToArray(), "X").Problems);
    }

    [Fact]
    public void Record_PointsAtTheHostedFiles() =>
        Assert.Equal("v=BIMI1; l=https://dotmarc.example/bimi/contoso.com/logo.svg; a=https://dotmarc.example/bimi/contoso.com/certificate.pem;",
            BimiHosting.Record(Host, "contoso.com", hasCertificate: true));

    [Fact]
    public async Task HostedFiles_AreServedPublicly_AndThePushPublishesTheRecord()
    {
        await using var host = await ApiTestHost.StartAsync(fixture);
        Domain domain;
        await using (var context = host.CreateContext())
        {
            domain = new Domain { Name = "contoso.com", FirstSeenUtc = DateTimeOffset.UtcNow };
            context.Domains.Add(domain);
            await context.SaveChangesAsync();

            var logo = SvgTinyPs.Prepare(Encoding.UTF8.GetBytes(DesignToolSvg), "Contoso").Svg!;
            Assert.Null(await BimiHosting.SaveLogoAsync(context, Actor, domain, logo));
            Assert.Equal("Upload the logo first.", await BimiHosting.SaveCertificateAsync(context, Actor, new Domain { Id = 999, Name = "x" }, [], DateTimeOffset.UtcNow));
            Assert.Equal(AuditActions.BimiLogoSaved, (await context.AuditEntries.SingleAsync()).Action);
        }

        using var client = host.Factory.CreateClient();
        var served = await client.GetAsync("/bimi/contoso.com/logo.svg");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("image/svg+xml", served.Content.Headers.ContentType?.MediaType);
        Assert.Contains("sandbox", served.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("<title>Contoso</title>", await served.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/bimi/contoso.com/certificate.pem")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/bimi/other.com/logo.svg")).StatusCode);

        var lookup = new FakeTxtRecordLookup();
        var builder = new BimiChangeBuilder(lookup, new FakeDbContextFactory(host.ConnectionString), Options.Create(new MtaStsOptions { HostingHostname = Host }));
        var plan = await builder.BuildAsync(new DnsPushRequest(domain, "cloudflare", "contoso.com", null), CancellationToken.None);
        var change = Assert.Single(plan.Changes);
        Assert.Equal((DnsRecordChangeKind.Create, "TXT", "default._bimi.contoso.com", BimiHosting.Record(Host, "contoso.com", false)),
            (change.Kind, change.RecordType, change.Name, change.DesiredValue));

        lookup.LookupsByName["default._bimi.contoso.com"] = new(BimiHosting.Record(Host, "contoso.com", false), null);
        Assert.Empty((await builder.BuildAsync(new DnsPushRequest(domain, "cloudflare", "contoso.com", null), CancellationToken.None)).Changes);

        await using (var context = host.CreateContext())
        {
            await BimiHosting.RemoveAsync(context, Actor, domain);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/bimi/contoso.com/logo.svg")).StatusCode);
        Assert.Equal("error", (await builder.BuildAsync(new DnsPushRequest(domain, "cloudflare", "contoso.com", null), CancellationToken.None)).Refusal);
    }
}
