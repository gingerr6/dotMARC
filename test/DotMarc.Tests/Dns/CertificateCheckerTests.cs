using System.Net.Security;
using DotMarc.Data;
using DotMarc.Dns;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class CertificateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static HostCertificate Cert(string host, int daysLeft, SslPolicyErrors errors = SslPolicyErrors.None) =>
        new(host, Now.AddDays(daysLeft), "Let's Encrypt", errors);

    [Fact]
    public void Nothing_on_https_is_no_website() =>
        Assert.Equal(CertificateCheckStatus.NoWebsite, CertificateChecker.Evaluate([], Now).Status);

    [Fact]
    public void Certificates_with_plenty_of_time_left_are_ok()
    {
        var result = CertificateChecker.Evaluate([Cert("contoso.com", 60), Cert("www.contoso.com", 45)], Now);

        Assert.Equal(CertificateCheckStatus.Ok, result.Status);
        Assert.Equal(Now.AddDays(45), result.EarliestExpiryUtc);
    }

    [Theory]
    [InlineData(10, CertificateCheckStatus.ExpiringSoon)]
    [InlineData(14, CertificateCheckStatus.ExpiringSoon)]
    [InlineData(15, CertificateCheckStatus.Ok)]
    [InlineData(-1, CertificateCheckStatus.Expired)]
    public void Expiry_is_judged_against_fourteen_days(int daysLeft, CertificateCheckStatus expected) =>
        Assert.Equal(expected, CertificateChecker.Evaluate([Cert("www.contoso.com", daysLeft)], Now).Status);

    [Fact]
    public void A_certificate_for_another_name_is_not_valid()
    {
        var result = CertificateChecker.Evaluate([Cert("contoso.com", 60, SslPolicyErrors.RemoteCertificateNameMismatch)], Now);

        Assert.Equal(CertificateCheckStatus.Invalid, result.Status);
        Assert.Contains("different name", result.Detail);
    }

    [Fact]
    public void The_worst_host_decides()
    {
        var result = CertificateChecker.Evaluate([Cert("contoso.com", 60), Cert("www.contoso.com", 5), Cert("mta-sts.contoso.com", -2)], Now);

        Assert.Equal(CertificateCheckStatus.Expired, result.Status);
        Assert.Contains("mta-sts.contoso.com: expired", result.Detail);
    }

    private sealed class FakeProbe(params HostCertificate[] certificates) : ITlsCertificateProbe
    {
        public List<string> Probed { get; } = [];

        public Task<HostCertificate?> ProbeAsync(string host, CancellationToken cancellationToken)
        {
            Probed.Add(host);
            return Task.FromResult(certificates.FirstOrDefault(certificate => certificate.Host == host));
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task It_checks_the_domain_www_and_mta_sts()
    {
        var probe = new FakeProbe(Cert("www.contoso.com", 90));

        var result = await new CertificateChecker(probe, new FixedTime()).CheckAsync("contoso.com", CancellationToken.None);

        Assert.Equal(["contoso.com", "www.contoso.com", "mta-sts.contoso.com"], probe.Probed);
        Assert.Equal(CertificateCheckStatus.Ok, result.Status);
    }
}
