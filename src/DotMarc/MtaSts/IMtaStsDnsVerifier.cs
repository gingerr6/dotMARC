namespace DotMarc.MtaSts;

public interface IMtaStsDnsVerifier
{
    Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken);

    /// <summary>Reads the domain's _mta-sts TXT record, which tells senders there's a policy to fetch.</summary>
    Task<MtaStsPolicyRecordCheck> CheckPolicyRecordAsync(string domainName, CancellationToken cancellationToken);
}
