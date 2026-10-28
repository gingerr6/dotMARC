namespace DotMarc.Senders;

/// <summary>Who a source is, for approving it: a recognised service (every IP it sends from at once), or else the one
/// IP. Key is what SenderApproval stores. A forwarder (see KnownSender.IsForwarder) counts as reviewed until someone
/// decides otherwise.</summary>
public sealed record SenderIdentity(string Key, string Label, bool IsService, bool IsForwarder = false)
{
    private const string IpPrefix = "ip:";

    /// <summary>Why an unrecognised IP was taken for a forwarder from its mail (see ForwardingDetector); null for a
    /// known forwarding service, which says so by name, and for everything else.</summary>
    public string? ForwardingReason { get; init; }

    /// <summary>detectedForwarding is ForwardingDetector's verdict on the IP's mail. It only applies to an IP that isn't a
    /// recognised service: a service such as Microsoft 365 is one approval for all its addresses, and some of its mail
    /// being forwarded through it doesn't make the rest of it a forwarder's.</summary>
    public static SenderIdentity For(string ip, string? hostName, string? organization, string? detectedForwarding = null) =>
        KnownSenders.Recognise(hostName, organization) is { } service
            ? new SenderIdentity(service.Key, service.Name, IsService: true, service.IsForwarder)
            : new SenderIdentity(IpPrefix + ip, ip, IsService: false, IsForwarder: detectedForwarding is not null) { ForwardingReason = detectedForwarding };

    /// <summary>A label for a stored key, for the audit log and alerts.</summary>
    public static string LabelFor(string key) =>
        key.StartsWith(IpPrefix, StringComparison.Ordinal) ? key[IpPrefix.Length..] : KnownSenders.Find(key)?.Name ?? key;
}
