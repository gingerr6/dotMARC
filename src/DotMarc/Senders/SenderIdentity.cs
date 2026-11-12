namespace DotMarc.Senders;

/// <summary>Who a source is, for approving it: a recognised service (every IP it sends from at once), or else the one
/// IP. Key is what SenderApproval stores. A forwarder (see KnownSender.IsForwarder) and a likely spoof (see
/// SpoofHeuristic) count as reviewed until someone decides otherwise. A saved decision replaces both.</summary>
public sealed record SenderIdentity(string Key, string Label, bool IsService, bool IsForwarder = false, bool IsLikelySpoof = false)
{
    private const string IpPrefix = "ip:";

    /// <summary>Why an unrecognised IP was taken for a forwarder from its mail (see ForwardingDetector); null for a
    /// known forwarding service, which says so by name, and for everything else.</summary>
    public string? ForwardingReason { get; init; }

    /// <summary>Why an unrecognised IP looks like spoofing (see SpoofHeuristic). Null when it doesn't, and when a
    /// forwarder or a recognised service took the source instead.</summary>
    public string? SpoofReason { get; init; }

    /// <summary>detectedForwarding is ForwardingDetector's verdict on the IP's mail. It only applies to an IP that isn't a
    /// recognised service: a service such as Microsoft 365 is one approval for all its addresses, and some of its mail
    /// being forwarded through it doesn't make the rest of it a forwarder's. spoof is the same idea for mail that
    /// looks forged. A recognised service ignores it, and detected forwarding wins over it, because that mail carries
    /// the domain's own signature.</summary>
    public static SenderIdentity For(string ip, string? hostName, string? organization, string? detectedForwarding = null, SpoofAssessment? spoof = null) =>
        KnownSenders.Recognise(hostName, organization, ip) is { } service
            ? new SenderIdentity(service.Key, service.Name, IsService: true, service.IsForwarder)
            : new SenderIdentity(IpPrefix + ip, ip, IsService: false, IsForwarder: detectedForwarding is not null,
                IsLikelySpoof: detectedForwarding is null && spoof is { IsLikelySpoof: true })
            {
                ForwardingReason = detectedForwarding,
                SpoofReason = detectedForwarding is null && spoof is { IsLikelySpoof: true } ? spoof.Reason : null,
            };

    /// <summary>A label for a stored key, for the audit log and alerts.</summary>
    public static string LabelFor(string key) =>
        key.StartsWith(IpPrefix, StringComparison.Ordinal) ? key[IpPrefix.Length..] : KnownSenders.Find(key)?.Name ?? key;
}
