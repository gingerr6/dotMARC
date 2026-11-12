using DotMarc.Data;

namespace DotMarc.Senders;

/// <summary>How a source is described on the Sources pages. A saved decision (approved, or not ours) replaces the
/// forwarder and likely-spoof labels. Until someone decides, those two are not waiting for review.</summary>
public static class SourceReview
{
    public const string LikelySpoofLabel = "Likely spoof";
    public const string ForwarderLabel = "Forwarder";

    public static bool CountsAsUnreviewed(SenderIdentity identity, SenderApprovalStatus? decision) =>
        decision is null && !identity.IsForwarder && !identity.IsLikelySpoof;

    public static string Label(SenderIdentity identity, SenderApprovalStatus? decision) =>
        decision is not null ? SenderApprovalService.Describe(decision)
        : identity.IsForwarder ? ForwarderLabel
        : identity.IsLikelySpoof ? LikelySpoofLabel
        : SenderApprovalService.Describe(null);
}
