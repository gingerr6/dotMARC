namespace DotMarc.Senders;

/// <summary>Who a source is, for approving it: a recognised service (every IP it sends from at once), or else the one
/// IP. Key is what SenderApproval stores.</summary>
public sealed record SenderIdentity(string Key, string Label, bool IsService)
{
    private const string IpPrefix = "ip:";

    public static SenderIdentity For(string ip, string? hostName, string? organization) =>
        KnownSenders.Recognise(hostName, organization) is { } service
            ? new SenderIdentity(service.Key, service.Name, IsService: true)
            : new SenderIdentity(IpPrefix + ip, ip, IsService: false);

    /// <summary>A label for a stored key, for the audit log and alerts.</summary>
    public static string LabelFor(string key) =>
        key.StartsWith(IpPrefix, StringComparison.Ordinal) ? key[IpPrefix.Length..] : KnownSenders.Find(key)?.Name ?? key;
}
