using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DotMarc.Data;
using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class BimiCheckerTests
{
    private const string GoodLogo = """
        <svg xmlns="http://www.w3.org/2000/svg" version="1.2" baseProfile="tiny-ps" viewBox="0 0 100 100">
          <title>Contoso</title>
          <rect width="100" height="100" fill="#123456"/>
        </svg>
        """;

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LogoProblems_AcceptsSvgTinyPs() => Assert.Empty(BimiChecker.LogoProblems(Encoding.UTF8.GetBytes(GoodLogo)));

    [Fact]
    public void LogoProblems_ListsWhatsWrongWithAnOrdinarySvg()
    {
        var problems = BimiChecker.LogoProblems(Encoding.UTF8.GetBytes(
            """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 100"><script>alert(1)</script><image href="https://x.example/a.png"/></svg>"""));

        Assert.Contains(problems, problem => problem.Contains("tiny-ps"));
        Assert.Contains(problems, problem => problem.Contains("<title>"));
        Assert.Contains(problems, problem => problem.Contains("scripts"));
        Assert.Contains(problems, problem => problem.Contains("outside the file"));
        Assert.Contains(problems, problem => problem.Contains("square"));
    }

    [Fact]
    public void LogoProblems_RefusesADtd() =>
        Assert.Equal(["it isn't a valid SVG file"], BimiChecker.LogoProblems(Encoding.UTF8.GetBytes("""<!DOCTYPE svg [<!ENTITY x "y">]><svg>&x;</svg>""")));

    [Fact]
    public void InspectCertificate_AcceptsACurrentMarkCertificate()
    {
        var (notAfter, problem) = BimiChecker.InspectCertificate(Pem(mark: true, Now.AddYears(1)), Now);

        Assert.Null(problem);
        Assert.Equal(Now.AddYears(1).Date, notAfter!.Value.Date);
    }

    [Fact]
    public void InspectCertificate_RefusesExpiredOrOrdinaryCertificates()
    {
        Assert.Contains("expired", BimiChecker.InspectCertificate(Pem(mark: true, Now.AddDays(-1)), Now).Problem);
        Assert.Contains("isn't a mark certificate", BimiChecker.InspectCertificate(Pem(mark: false, Now.AddYears(1)), Now).Problem);
        Assert.Contains("isn't a PEM", BimiChecker.InspectCertificate("nonsense"u8.ToArray(), Now).Problem);
    }

    [Theory]
    [InlineData(DmarcPolicyLevel.Reject, null, true)]
    [InlineData(DmarcPolicyLevel.Quarantine, 100, true)]
    [InlineData(DmarcPolicyLevel.Quarantine, 50, false)]
    [InlineData(DmarcPolicyLevel.None, null, false)]
    public void IsEnforced_NeedsQuarantineOrRejectForAllMail(DmarcPolicyLevel policy, int? percent, bool expected) =>
        Assert.Equal(expected, BimiChecker.IsEnforced(new Domain { Name = "contoso.com", DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = policy, DmarcPercent = percent }));

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("10.0.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void IsPublic_RefusesPrivateAndInternalAddresses(string address, bool expected) =>
        Assert.Equal(expected, PublicHttpFetcher.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task CheckAsync_WithNoRecord_SaysWhetherDmarcIsReadyForIt()
    {
        var checker = Checker(txt: null);

        Assert.Contains("Ready for BIMI", (await checker.CheckAsync(Enforced(), CancellationToken.None)).Detail);
        var notReady = await checker.CheckAsync(new Domain { Name = "contoso.com", DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.None }, CancellationToken.None);
        Assert.Equal(BimiCheckStatus.NoRecord, notReady.Status);
        Assert.Contains("Not ready", notReady.Detail);
    }

    [Fact]
    public async Task CheckAsync_WithAGoodLogoAndCertificate_IsOk()
    {
        var checker = Checker("v=BIMI1; l=https://cdn.contoso.com/logo.svg; a=https://cdn.contoso.com/vmc.pem",
            ("https://cdn.contoso.com/logo.svg", Encoding.UTF8.GetBytes(GoodLogo)),
            ("https://cdn.contoso.com/vmc.pem", Pem(mark: true, Now.AddYears(1))));

        var result = await checker.CheckAsync(Enforced(), CancellationToken.None);

        Assert.Equal((BimiCheckStatus.Ok, "https://cdn.contoso.com/logo.svg"), (result.Status, result.LogoUrl));
        Assert.NotNull(result.CertificateExpiresUtc);
    }

    [Fact]
    public async Task CheckAsync_WithoutACertificate_IsLogoOnly_AndWaitsOnDmarcWhenNotEnforced()
    {
        var checker = Checker("v=BIMI1; l=https://cdn.contoso.com/logo.svg", ("https://cdn.contoso.com/logo.svg", Encoding.UTF8.GetBytes(GoodLogo)));

        Assert.Equal(BimiCheckStatus.LogoOnly, (await checker.CheckAsync(Enforced(), CancellationToken.None)).Status);
        var monitoring = Enforced();
        monitoring.DmarcPolicy = DmarcPolicyLevel.None;
        Assert.Equal(BimiCheckStatus.NotEnforced, (await checker.CheckAsync(monitoring, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task CheckAsync_RefusesALogoThatIsntHttps()
    {
        var result = await Checker("v=BIMI1; l=http://cdn.contoso.com/logo.svg").CheckAsync(Enforced(), CancellationToken.None);

        Assert.Equal(BimiCheckStatus.Misconfigured, result.Status);
        Assert.Contains("https", result.Detail);
    }

    private static Domain Enforced() => new() { Name = "contoso.com", DmarcCheckStatus = DmarcCheckStatus.Ok, DmarcPolicy = DmarcPolicyLevel.Reject };

    private static BimiChecker Checker(string? txt, params (string Url, byte[] Body)[] files)
    {
        var dnsJson = txt is null ? """{"Status":3}""" : $$"""{"Status":0,"Answer":[{"type":16,"data":"\"{{txt}}\""}]}""";
        var dns = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(dnsJson) })) { BaseAddress = new Uri("https://dns.example/") };
        var web = new HttpClient(new StubHandler(request =>
            files.FirstOrDefault(file => file.Url == request.RequestUri!.ToString()) is { Body: { } body }
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        return new BimiChecker(dns, new PublicHttpFetcher(web), new FixedTimeProvider(Now));
    }

    private static byte[] Pem(bool mark, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Contoso Ltd", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(mark ? BimiChecker.BimiKeyUsageOid : "1.3.6.1.5.5.7.3.1")], false));
        using var certificate = request.CreateSelfSigned(notAfter.AddYears(-2), notAfter);
        return Encoding.ASCII.GetBytes(certificate.ExportCertificatePem());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
