using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DotMarc.Senders;

/// <summary>What <see cref="SpoofHeuristic"/> reads. AnyAlignedPass is true when any message from the source passed
/// aligned SPF or aligned DKIM (the report's policy-evaluated result). HostNameResolved is true once a reverse-DNS
/// lookup has been stored, including a lookup that found no name. ListedOnBlocklist is true only when this app has
/// already recorded that this address is listed.</summary>
public sealed record SpoofEvidence(string Ip, int Volume, bool AnyAlignedPass, string? HostName, bool HostNameResolved, bool ListedOnBlocklist);

/// <summary>The heuristic's answer. Reason is a sentence for the tooltip; null when the source is not flagged.</summary>
public sealed record SpoofAssessment(bool IsLikelySpoof, string? Reason)
{
    public static SpoofAssessment No { get; } = new(false, null);
}

/// <summary>Marks an unrecognised source as likely spoofing so it can be set aside, the way a known forwarder is.
/// It is deliberately strict. Mail that passed aligned SPF or DKIM is never flagged. A burst above
/// <see cref="MaxVolume"/> is left for a person even when the address looks residential, because that much mail might
/// be a real incident. A named mail server (mail.contoso.com) that simply fails authentication is left for review
/// too: it may be a sender the domain hasn't set up yet. A decision someone saves replaces the flag.</summary>
public static class SpoofHeuristic
{
    /// <summary>At or under this many messages, and only with the other signals, a source can be set aside. Above it,
    /// the source still needs a person. The same size as a stray sender in EnforcementReadiness, which uses 50.</summary>
    public const int MaxVolume = 50;

    // Tokens that show up in subscriber reverse-DNS labels (a hyphen or a digit may follow). "dyn" on its own is not
    // here: that label is also the mail brand dyn.com. An embedded copy of the IP, below, still catches the usual
    // "dyn-1-2-3-4.isp.net" form.
    private static readonly string[] DynamicLabels =
        ["dynamic", "dynip", "dsl", "adsl", "vdsl", "dialup", "dial-up", "dhcp", "pppoe", "broadband", "residential", "pool"];

    // ISP reverse zones handed out to subscriber lines, not to a company's mail servers. "comcast.net" itself is not
    // listed: Comcast's own mail could live there, while hsd1.comcast.net is the subscriber zone.
    private static readonly string[] ResidentialSuffixes = ["res.rr.com", "dyn.rr.com", "hsd1.comcast.net", "myvzw.com"];

    public static SpoofAssessment Assess(SpoofEvidence evidence)
    {
        if (evidence.AnyAlignedPass || evidence.Volume <= 0 || evidence.Volume > MaxVolume)
        {
            return SpoofAssessment.No;
        }

        var noName = evidence.HostNameResolved && string.IsNullOrWhiteSpace(evidence.HostName);
        var dynamic = !string.IsNullOrWhiteSpace(evidence.HostName) && LooksDynamicOrResidential(evidence.HostName, evidence.Ip);
        if (!noName && !dynamic && !evidence.ListedOnBlocklist)
        {
            return SpoofAssessment.No;
        }

        // A blocklist hit on its own is not enough. The check this app stores is of the domain's own servers (its SPF
        // addresses, approved senders and mail exchangers). Hiding one of those because it was listed would hide the
        // domain's own mail. The listing is mentioned only when the address already looks unprovisioned.
        if (!noName && !dynamic)
        {
            return SpoofAssessment.No;
        }

        var signals = new List<string>();
        if (noName)
        {
            signals.Add("it has no reverse DNS name");
        }
        else if (dynamic)
        {
            signals.Add($"its reverse DNS name ({evidence.HostName!.Trim().TrimEnd('.')}) looks like a residential or dynamic connection");
        }

        if (evidence.ListedOnBlocklist)
        {
            signals.Add("a blocklist dotMARC already checked lists this address");
        }

        var volume = evidence.Volume == 1 ? "1 message" : $"{evidence.Volume} messages";
        return new SpoofAssessment(true,
            $"Likely spoofing: it isn't a recognised sender, both SPF and DKIM failed, and there's very little of it ({volume}). Also, {string.Join(", and ", signals)}. Approving it or marking it not ours replaces this.");
    }

    /// <summary>True when the blocklist detail text names this IP as its own token, not as part of a longer address.</summary>
    public static bool DetailMentionsIp(string? detail, string ip)
    {
        if (string.IsNullOrEmpty(detail) || string.IsNullOrWhiteSpace(ip))
        {
            return false;
        }

        var index = 0;
        while ((index = detail.IndexOf(ip, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var before = index == 0 || !IsIpCharacter(detail[index - 1]);
            var after = index + ip.Length >= detail.Length || !IsIpCharacter(detail[index + ip.Length]);
            if (before && after)
            {
                return true;
            }

            index += ip.Length;
        }

        return false;
    }

    /// <summary>A generic subscriber PTR: the address is written into the name, a label is a well-known dynamic token,
    /// or the name is under a known residential reverse zone.</summary>
    public static bool LooksDynamicOrResidential(string hostName, string ip)
    {
        var host = hostName.Trim().TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (ResidentialSuffixes.Any(suffix => host.Equals(suffix, StringComparison.Ordinal) || host.EndsWith("." + suffix, StringComparison.Ordinal)))
        {
            return true;
        }

        if (EmbedsAddress(host, ip))
        {
            return true;
        }

        foreach (var label in host.Split('.'))
        {
            if (IsDynamicLabel(label))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDynamicLabel(string label)
    {
        foreach (var token in DynamicLabels)
        {
            if (label.Equals(token, StringComparison.Ordinal))
            {
                return true;
            }

            // "hsd1" and "pool-12", but not "mailpool" or "dynect".
            if (label.StartsWith(token, StringComparison.Ordinal) && label.Length > token.Length && label[token.Length] is '-' or >= '0' and <= '9')
            {
                return true;
            }
        }

        // hsd1.comcast.net's first label. "hsd" alone is not a token: it would be too short.
        return label.StartsWith("hsd", StringComparison.Ordinal) && label.Length > 3 && char.IsDigit(label[3]);
    }

    private static bool EmbedsAddress(string host, string ip)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return false;
        }

        address = CidrMatcher.Normalise(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return EmbedsIpv6(host, address);
        }

        var octets = address.GetAddressBytes().Select(part => part.ToString(CultureInfo.InvariantCulture)).ToArray();
        var forwardDash = string.Join('-', octets);
        var reverseDash = string.Join('-', octets.Reverse());
        var forwardDot = string.Join('.', octets);
        return host.Contains(forwardDash, StringComparison.Ordinal)
            || host.Contains(reverseDash, StringComparison.Ordinal)
            || host.Contains(forwardDot, StringComparison.Ordinal);
    }

    /// <summary>A generic IPv6 PTR writes several hextets into the name. A run of zeros is ignored, so a name that
    /// merely contains "0-0-0-0" is not treated as dynamic.</summary>
    private static bool EmbedsIpv6(string host, IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        var hextets = new string[8];
        for (var i = 0; i < 8; i++)
        {
            hextets[i] = ((bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToString("x", CultureInfo.InvariantCulture);
        }

        for (var start = 0; start <= 4; start++)
        {
            var slice = hextets.Skip(start).Take(4).ToArray();
            if (slice.Count(part => part != "0") < 3)
            {
                continue;
            }

            if (host.Contains(string.Join('-', slice), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIpCharacter(char character) =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F' or '.' or ':';
}
