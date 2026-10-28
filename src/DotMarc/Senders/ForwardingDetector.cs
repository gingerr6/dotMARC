using DotMarc.Data;

namespace DotMarc.Senders;

/// <summary>One report record, cut down to what <see cref="ForwardingDetector"/> reads: the domains SPF was checked for
/// (the envelope sender) and the DKIM signatures' domains and results.</summary>
public sealed record ForwardingEvidence(int MessageCount, string HeaderFrom, AuthResult SpfResult, IReadOnlyList<string> SpfDomains,
    IReadOnlyList<(string Domain, DmarcMechanismResult Result)> DkimSignatures, IReadOnlyList<DmarcPolicyOverrideType> OverrideTypes)
{
    public static ForwardingEvidence From(ReportRecord record) => new(record.MessageCount, record.HeaderFrom, record.SpfResult,
        record.AuthDetails.Where(detail => detail.Mechanism == DmarcAuthMechanism.Spf).Select(detail => detail.Domain).ToList(),
        record.AuthDetails.Where(detail => detail.Mechanism == DmarcAuthMechanism.Dkim).Select(detail => (detail.Domain, detail.Result)).ToList(),
        record.OverrideReasons.Select(reason => reason.Type).ToList());
}

/// <summary>Tells mail the domain really sent, but that reached the receiver through someone else's server, from mail an
/// impostor sent. Forwarding (a mailbox rule, an alias, a mailing list, or a recipient's filtering service such as
/// Avanan or Perception Point) keeps the domain's DKIM signature on the message but sends it from a server that isn't
/// in the domain's SPF. Either the signature breaks because the message was changed on the way (a rewritten link, a
/// banner), which a sender never does to its own mail, or it survives and the envelope sender is still the domain's,
/// which a service sending for the domain directly would have in its SPF. An impostor can't sign as the domain, so
/// their mail has no signature of the domain's at all. Receivers also sometimes say outright that mail was forwarded.
/// Mail a forwarder re-addresses to its own envelope (SRS) with the signature intact can't be told from a service the
/// domain uses, so isn't counted.</summary>
public static class ForwardingDetector
{
    private static readonly DmarcPolicyOverrideType[] ForwardedOverrides =
        [DmarcPolicyOverrideType.Forwarded, DmarcPolicyOverrideType.TrustedForwarder, DmarcPolicyOverrideType.MailingList];

    /// <summary>Why a source's mail looks forwarded, or null when it doesn't: most of its messages must carry the
    /// domain's own signature without passing SPF, or have been reported as forwarded. totalVolume is all the source's
    /// messages; records need only include the ones that didn't pass SPF.</summary>
    public static string? Detect(int totalVolume, IEnumerable<ForwardingEvidence> records)
    {
        if (totalVolume <= 0)
        {
            return null;
        }

        int reported = 0, intact = 0, broken = 0;
        foreach (var record in records.Where(record => record.SpfResult != AuthResult.Pass))
        {
            if (record.OverrideTypes.Any(ForwardedOverrides.Contains))
            {
                reported += record.MessageCount;
                continue;
            }

            var own = record.DkimSignatures.Where(signature => signature.Result != DmarcMechanismResult.None && Aligned(signature.Domain, record.HeaderFrom)).ToList();
            if (own.Any(signature => signature.Result == DmarcMechanismResult.Pass))
            {
                if (record.SpfDomains.Any(domain => Aligned(domain, record.HeaderFrom)))
                {
                    intact += record.MessageCount;
                }
            }
            else if (own.Count > 0)
            {
                broken += record.MessageCount;
            }
        }

        if ((reported + intact + broken) * 2 < totalVolume)
        {
            return null;
        }

        return reported >= intact && reported >= broken
            ? "Receivers reported this mail as forwarded."
            : broken > intact
                ? "Its mail carries the domain's own DKIM signature, broken on the way, from a server not in the domain's SPF: the domain's real mail, forwarded or passed through a filtering service that changed it. An impostor couldn't have signed it."
                : "Its mail carries the domain's own DKIM signature and envelope sender, from a server not in the domain's SPF: the domain's real mail, forwarded on. An impostor couldn't have signed it.";
    }

    /// <summary>Relaxed alignment by the same-or-subdomain rule DomainStatistics uses (no public suffix list).</summary>
    private static bool Aligned(string signingDomain, string headerFrom)
    {
        var signer = signingDomain.Trim().TrimEnd('.').ToLowerInvariant();
        var from = headerFrom.Trim().TrimEnd('.').ToLowerInvariant();
        return signer.Length > 0 && (signer == from || signer.EndsWith("." + from, StringComparison.Ordinal) || from.EndsWith("." + signer, StringComparison.Ordinal));
    }
}
