using System.Net;

namespace DotMarc.Senders;

/// <summary>A mail service dotMARC recognises. Key is stored on SenderApproval, so it must never change once shipped.
/// A forwarder only passes on mail someone else sent (a recipient's address that forwards elsewhere, or a mailing
/// list), so it's never the domain's own sender and needs no review. Cidrs are published ranges for that service,
/// dated in the comments below. They are only used when the source has no forward-confirmed hostname.</summary>
public sealed record KnownSender(string Key, string Name, string[] HostSuffixes, string[] OrganizationKeywords, bool IsForwarder = false, string[]? Cidrs = null);

/// <summary>Recognises the mail services behind source IPs, so a domain's senders can be approved a service at a time
/// rather than an IP at a time. A source's forward-confirmed reverse DNS name (see ReverseDnsLookup) is trusted first,
/// and the longest matching suffix wins, so a more specific name (unverified-forwarding.1e100.net) is not hidden by a
/// shorter one listed earlier (1e100.net). Published CIDR ranges apply only when that name is missing. The RDAP owner
/// is only used for companies that do nothing but mail, since a cloud provider's owner name (Microsoft, Google,
/// Amazon) covers every customer's virtual machine too.</summary>
public static class KnownSenders
{
    public static IReadOnlyList<KnownSender> All { get; } =
    [
        new("microsoft365", "Microsoft 365", ["outbound.protection.outlook.com", "protection.outlook.com"], []),
        // Google Workspace customer's own mail. Google Groups uses these same PTR names, so it can't be split out
        // without also calling Workspace mail a mailing list. See the unverified-forwarding entry for the one Google
        // pool that does have its own hostname.
        new("google", "Google Workspace", ["google.com", "googlemail.com", "1e100.net"], []),
        new("amazonses", "Amazon SES", ["amazonses.com"], []),
        new("mailchimp", "Mailchimp", ["mcsv.net", "mcdlv.net", "rsgsv.net", "mandrillapp.com"], ["Rocket Science Group"]),
        new("smtp2go", "SMTP2GO", ["smtp2go.com", "smtp2go.net", "smtpcorp.com"], ["SMTP2GO", "Smtpcorp"]),
        new("sendgrid", "SendGrid", ["sendgrid.net"], ["SendGrid"]),
        new("mailgun", "Mailgun", ["mailgun.net", "mailgun.org"], ["Mailgun"]),
        new("postmark", "Postmark", ["mtasv.net"], ["Wildbit", "ActiveCampaign Postmark"]),
        new("sparkpost", "SparkPost", ["sparkpostmail.com"], ["SparkPost", "MessageBird"]),
        new("brevo", "Brevo", ["sendinblue.com", "brevo.com"], ["Sendinblue", "Brevo"]),
        new("mimecast", "Mimecast", ["mimecast.com", "mimecast-offshore.com"], ["Mimecast"]),
        new("proofpoint", "Proofpoint", ["pphosted.com", "ppe-hosted.com", "ppops.net"], ["Proofpoint"]),
        new("barracuda", "Barracuda", ["barracudanetworks.com", "cudamail.com"], ["Barracuda"]),
        new("salesforce", "Salesforce", ["salesforce.com", "exacttarget.com"], []),
        new("hubspot", "HubSpot", ["hubspotemail.net", "hubspot.com", "hubspot.net"], ["HubSpot"]),
        new("zendesk", "Zendesk", ["zendesk.com"], ["Zendesk"]),
        new("freshworks", "Freshworks", ["freshdesk.com", "freshworks.com"], []),
        new("zoho", "Zoho Mail", ["zoho.com", "zoho.eu", "zohomail.com"], []),
        new("constantcontact", "Constant Contact", ["constantcontact.com", "confirmedcc.com"], ["Constant Contact"]),
        new("campaignmonitor", "Campaign Monitor", ["createsend.com", "cmail1.com", "cmail2.com", "cmail19.com", "cmail20.com"], []),

        // Forwarding and mailing-list services. A hostname match beats a range, and a longer suffix beats a shorter
        // one, which is why Google's unverified-forwarding pool can sit here, after the shorter 1e100.net suffix.
        //
        // Microsoft's relay pool (mail forwarded out of Microsoft 365 that failed authentication on the way in) is
        // not listed. The published article says the pool is unpublished because it changes often, and the PTR names
        // that are documented (*.outbound.protection.outlook.com) are the same ones ordinary Microsoft 365 mail uses,
        // so a hostname can't tell them apart:
        // https://learn.microsoft.com/en-us/defender-office-365/outbound-spam-high-risk-delivery-pool-about
        // (checked 2026-11-12).
        //
        // Cloudflare Email Service outbound prefixes, from
        // https://developers.cloudflare.com/email-service/reference/postmaster/ (checked 2026-11-12). Routing and
        // sending share these hostnames and prefixes; a missing name is classified the same way a matching name is.
        // The same page's SPF example shows a narrower 104.30.0.0/20; the ranges below are the allow-list Cloudflare
        // tells postmasters to permit.
        new("cloudflare-email-routing", "Cloudflare Email Routing",
            ["cloudflare-email.net", "cloudflare-email.org", "cloudflare-email.com"], [], IsForwarder: true,
            Cidrs: ["104.30.0.0/19", "2405:8100:c000::/38"]),
        new("improvmx", "ImprovMX", ["improvmx.com"], [], IsForwarder: true),
        new("forwardemail", "Forward Email", ["forwardemail.net"], [], IsForwarder: true),
        new("simplelogin", "SimpleLogin", ["simplelogin.co", "simplelogin.com"], [], IsForwarder: true),
        new("addy", "addy.io", ["anonaddy.me", "addy.io"], [], IsForwarder: true),
        new("duckduckgo-email", "DuckDuckGo Email Protection", ["duck.com"], [], IsForwarder: true),
        new("pobox", "Pobox", ["pobox.com"], [], IsForwarder: true),
        // Filtering services in front of a recipient's mailbox: they re-send the domain's mail into it after scanning.
        new("avanan", "Check Point Harmony Email (Avanan)", ["cloud-sec-av.com", "avanan.net"], [], IsForwarder: true),
        new("perception-point", "Perception Point", ["perception-point.io"], [], IsForwarder: true),
        // Gmail routes unverified forwarding (a filter that forwards mail it could not authenticate) through servers
        // whose names end in unverified-forwarding.1e100.net. Those addresses are deliberately not in Google's SPF
        // record. Ranges last updated by Google on 2026-03-24; page checked 2026-11-12:
        // https://knowledge.workspace.google.com/admin/gmail/advanced/google-ip-address-ranges-for-outbound-mail-servers
        // Ordinary Workspace mail (me-in-f20.1e100.net, *.google.com) stays the "google" sender. The owner name
        // "Google LLC" is not a keyword: it also covers customers' virtual machines.
        new("google-unverified-forwarding", "Google unverified forwarding", ["unverified-forwarding.1e100.net"], [],
            IsForwarder: true, Cidrs: ["108.177.16.0/23", "142.250.220.0/23", "2600:1901:101::/48"]),
        // Groups.io mailing lists. Outbound mail uses their own hosts (for example mail02.groups.io) and the
        // addresses their help centre publishes for allow-listing, checked 2026-11-12:
        // https://groups.io/helpcenter/manual/generalinfo/general/mail_server_IP_addresses.htm
        new("groups-io", "Groups.io", ["groups.io"], [], IsForwarder: true, Cidrs:
        [
            "66.175.222.12/32", "66.175.222.108/32", "45.79.227.220/32", "45.79.224.9/32", "45.79.224.7/32",
            "192.53.124.123/32", "173.255.243.56/32", "192.53.124.254/32",
        ]),
    ];

    /// <summary>The service behind a source, or null when it isn't one dotMARC knows. A forward-confirmed hostname
    /// wins over a published range, including when the name matches nothing: a customer's own server that happens to
    /// sit in a published block keeps its own name. <paramref name="ip"/> is only consulted when the hostname is
    /// missing.</summary>
    public static KnownSender? Recognise(string? hostName, string? organization, string? ip = null)
    {
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var host = hostName.Trim().TrimEnd('.');
            KnownSender? best = null;
            var bestLength = -1;
            foreach (var sender in All)
            {
                foreach (var suffix in sender.HostSuffixes)
                {
                    if (suffix.Length > bestLength && HostMatches(host, suffix))
                    {
                        best = sender;
                        bestLength = suffix.Length;
                    }
                }
            }

            if (best is not null)
            {
                return best;
            }
        }
        else if (MatchCidr(ip) is { } byRange)
        {
            return byRange;
        }

        if (!string.IsNullOrWhiteSpace(organization))
        {
            foreach (var sender in All)
            {
                if (sender.OrganizationKeywords.Any(keyword => organization.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
                {
                    return sender;
                }
            }
        }

        return null;
    }

    public static KnownSender? Find(string key) => All.FirstOrDefault(sender => sender.Key == key);

    private static bool HostMatches(string host, string suffix) =>
        host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The service whose published range contains the IP, preferring the longest prefix when more than one
    /// could. Null when the text isn't an IP or no range contains it.</summary>
    private static KnownSender? MatchCidr(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address))
        {
            return null;
        }

        KnownSender? best = null;
        var bestLength = -1;
        foreach (var sender in All)
        {
            if (sender.Cidrs is null)
            {
                continue;
            }

            foreach (var cidr in sender.Cidrs)
            {
                if (CidrMatcher.Contains(cidr, address, out var length) && length > bestLength)
                {
                    best = sender;
                    bestLength = length;
                }
            }
        }

        return best;
    }
}
