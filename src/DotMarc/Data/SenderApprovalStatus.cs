namespace DotMarc.Data;

/// <summary>What someone decided about one of a domain's senders. Stored as text, so the names must never change.</summary>
public enum SenderApprovalStatus
{
    /// <summary>The domain's own mail: a service it uses, or its own server.</summary>
    Approved,

    /// <summary>Not the domain's mail: spoofing, or a forwarder that was looked at and accepted.</summary>
    NotOurs,
}
