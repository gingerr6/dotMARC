namespace DotMarc.MtaSts;

/// <summary>What a domain's _mta-sts TXT record says. Without exactly one valid record, senders don't look for the
/// policy at all (RFC 8461 §3.1), however well it's hosted.</summary>
public enum MtaStsPolicyRecordState
{
    Present,
    Missing,
    Invalid,
    Multiple
}

/// <summary>The outcome of IMtaStsDnsVerifier.CheckPolicyRecordAsync; Id is the record's id when it has one.</summary>
public sealed record MtaStsPolicyRecordCheck(MtaStsPolicyRecordState State, string? Id = null);
