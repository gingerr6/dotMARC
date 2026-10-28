namespace DotMarc.Senders;

/// <summary>A mail service dotMARC recognises. Key is stored on SenderApproval, so it must never change once shipped.
/// A forwarder only passes on mail someone else sent (a recipient's address that forwards elsewhere), so it's never the
/// domain's own sender and needs no review.</summary>
public sealed record KnownSender(string Key, string Name, string[] HostSuffixes, string[] OrganizationKeywords, bool IsForwarder = false);

/// <summary>Recognises the mail services behind source IPs, so a domain's senders can be approved a service at a time
/// rather than an IP at a time. A source's forward-confirmed reverse DNS name (see ReverseDnsLookup) is trusted first.
/// The RDAP owner is only used for companies that do nothing but mail, since a cloud provider's owner name (Microsoft,
/// Google, Amazon) covers every customer's virtual machine too.</summary>
public static class KnownSenders
{
    public static IReadOnlyList<KnownSender> All { get; } =
    [
        new("microsoft365", "Microsoft 365", ["outbound.protection.outlook.com", "protection.outlook.com"], []),
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

        // Forwarding-only services. Google's and Microsoft's own forwarding comes from the same servers as their
        // customers' mail, so it can't be told apart here and stays with those services.
        new("cloudflare-email-routing", "Cloudflare Email Routing", ["cloudflare-email.net"], [], IsForwarder: true),
        new("improvmx", "ImprovMX", ["improvmx.com"], [], IsForwarder: true),
        new("forwardemail", "Forward Email", ["forwardemail.net"], [], IsForwarder: true),
        new("simplelogin", "SimpleLogin", ["simplelogin.co", "simplelogin.com"], [], IsForwarder: true),
        new("addy", "addy.io", ["anonaddy.me", "addy.io"], [], IsForwarder: true),
        new("duckduckgo-email", "DuckDuckGo Email Protection", ["duck.com"], [], IsForwarder: true),
        new("pobox", "Pobox", ["pobox.com"], [], IsForwarder: true),
        // Filtering services in front of a recipient's mailbox: they re-send the domain's mail into it after scanning.
        new("avanan", "Check Point Harmony Email (Avanan)", ["cloud-sec-av.com", "avanan.net"], [], IsForwarder: true),
        new("perception-point", "Perception Point", ["perception-point.io"], [], IsForwarder: true),
    ];

    /// <summary>The service behind a source, or null when it isn't one dotMARC knows.</summary>
    public static KnownSender? Recognise(string? hostName, string? organization)
    {
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var host = hostName.Trim().TrimEnd('.');
            foreach (var sender in All)
            {
                if (sender.HostSuffixes.Any(suffix => host.Equals(suffix, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)))
                {
                    return sender;
                }
            }
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
}
