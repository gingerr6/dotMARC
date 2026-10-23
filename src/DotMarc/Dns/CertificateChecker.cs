using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Dns;

public sealed record CertificateCheckResult(CertificateCheckStatus Status, DateTimeOffset? EarliestExpiryUtc, string? Detail);

public interface ICertificateChecker
{
    Task<CertificateCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}

/// <summary>The certificate one host presents on HTTPS, and what was wrong with it.</summary>
public sealed record HostCertificate(string Host, DateTimeOffset NotAfterUtc, string Issuer, SslPolicyErrors Errors);

/// <summary>Fetches the certificate a host presents on port 443, or null when it doesn't answer on HTTPS.</summary>
public interface ITlsCertificateProbe
{
    Task<HostCertificate?> ProbeAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Checks the certificates on the domain's website (the domain itself and www) and its mta-sts host: an
/// expired or soon-expiring certificate breaks the site for visitors, and on mta-sts it stops senders fetching the
/// MTA-STS policy. Mail servers' own certificates are covered by the TLS reports instead.</summary>
public sealed class CertificateChecker(ITlsCertificateProbe probe, TimeProvider timeProvider) : ICertificateChecker
{
    /// <summary>Let's Encrypt and most automatic renewal renew with 30 days left, so under this it's usually failing.</summary>
    public const int ExpiringSoonDays = 14;

    public static IReadOnlyList<string> HostsFor(string domainName) => [domainName, $"www.{domainName}", $"mta-sts.{domainName}"];

    public async Task<CertificateCheckResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var certificates = new List<HostCertificate>();
        foreach (var host in HostsFor(domainName))
        {
            if (await probe.ProbeAsync(host, cancellationToken).ConfigureAwait(false) is { } certificate)
            {
                certificates.Add(certificate);
            }
        }

        return Evaluate(certificates, timeProvider.GetUtcNow());
    }

    /// <summary>The worst of the hosts' certificates decides: expired, then not valid for the host, then expiring
    /// soon.</summary>
    public static CertificateCheckResult Evaluate(IReadOnlyList<HostCertificate> certificates, DateTimeOffset nowUtc)
    {
        if (certificates.Count == 0)
        {
            return new CertificateCheckResult(CertificateCheckStatus.NoWebsite, null, "Nothing answers on HTTPS at the domain, www or mta-sts.");
        }

        var soon = nowUtc.AddDays(ExpiringSoonDays);
        CertificateCheckStatus StatusOf(HostCertificate certificate) =>
            certificate.NotAfterUtc <= nowUtc ? CertificateCheckStatus.Expired
            : certificate.Errors != SslPolicyErrors.None ? CertificateCheckStatus.Invalid
            : certificate.NotAfterUtc <= soon ? CertificateCheckStatus.ExpiringSoon
            : CertificateCheckStatus.Ok;

        var worst = certificates.Select(StatusOf).OrderBy(Severity).First();
        var detail = string.Join("; ", certificates.Select(certificate => Describe(certificate, StatusOf(certificate), nowUtc)));
        return new CertificateCheckResult(worst, certificates.Min(certificate => certificate.NotAfterUtc), detail);
    }

    private static int Severity(CertificateCheckStatus status) => status switch
    {
        CertificateCheckStatus.Expired => 0,
        CertificateCheckStatus.Invalid => 1,
        CertificateCheckStatus.ExpiringSoon => 2,
        _ => 3,
    };

    private static string Describe(HostCertificate certificate, CertificateCheckStatus status, DateTimeOffset nowUtc)
    {
        var when = UkTime.Date(certificate.NotAfterUtc);
        return status switch
        {
            CertificateCheckStatus.Expired => $"{certificate.Host}: expired {when}",
            CertificateCheckStatus.Invalid => $"{certificate.Host}: {Problem(certificate.Errors)} (expires {when})",
            CertificateCheckStatus.ExpiringSoon => $"{certificate.Host}: expires {when}, in {Math.Max(0, (int)(certificate.NotAfterUtc - nowUtc).TotalDays)} days",
            _ => $"{certificate.Host}: expires {when} ({certificate.Issuer})",
        };
    }

    private static string Problem(SslPolicyErrors errors) =>
        errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "certificate is for a different name" : "certificate isn't trusted";
}

/// <summary>Connects to a host on 443 and reads the certificate it presents, without trusting it. Only public
/// addresses are connected to: the domains are entered by people, and a name pointed at an internal address mustn't
/// make dotMARC probe its own network.</summary>
public sealed class TlsCertificateProbe : ITlsCertificateProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public async Task<HostCertificate?> ProbeAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            return null;
        }

        if (addresses.Length == 0 || addresses.Any(address => !PublicHttpFetcher.IsPublic(address)))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        using var client = new TcpClient(addresses[0].AddressFamily);
        try
        {
            await client.ConnectAsync(addresses[0], 443, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        X509Certificate2? presented = null;
        var errors = SslPolicyErrors.None;
        await using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, policyErrors) =>
        {
            presented = certificate is null ? null : X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            errors = policyErrors;
            return true;
        });
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is System.Security.Authentication.AuthenticationException or IOException
            or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The handshake can still fail after the certificate was seen; that certificate is what's reported.
        }

        using (presented)
        {
            return presented is null ? null : new HostCertificate(host, new DateTimeOffset(presented.NotAfter.ToUniversalTime(), TimeSpan.Zero), presented.GetNameInfo(X509NameType.SimpleName, forIssuer: true), errors);
        }
    }
}
