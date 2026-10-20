using DotMarc.Data;
using DotMarc.MtaSts;

namespace DotMarc.Tests.Internal;

internal sealed class FakeMtaStsDnsVerifier : IMtaStsDnsVerifier
{
    private static readonly Domain DefaultDomain = new() { Name = "default.example" };

    public MtaStsDnsVerificationResult Result { get; set; } = MtaStsDnsVerificationResult.Resolved;
    public List<string> VerifiedDomains { get; } = [];

    /// <summary>By default, a valid record whose id matches a domain's default policy settings.</summary>
    public MtaStsPolicyRecordCheck PolicyRecord { get; set; } = new(MtaStsPolicyRecordState.Present,
        MtaStsPolicyRenderer.PolicyId(DefaultDomain.MtaStsMode, DefaultDomain.MtaStsMxHosts, DefaultDomain.MtaStsMaxAgeSeconds));

    public Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken)
    {
        VerifiedDomains.Add(domainName);
        return Task.FromResult(Result);
    }

    public Task<MtaStsPolicyRecordCheck> CheckPolicyRecordAsync(string domainName, CancellationToken cancellationToken) =>
        Task.FromResult(PolicyRecord);
}
