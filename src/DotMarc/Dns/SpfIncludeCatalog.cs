namespace DotMarc.Dns;

/// <summary>The SPF includes of common mail services, for the SPF editor's "add an include" list and its
/// suggestions for a domain with no record yet.</summary>
public static class SpfIncludeCatalog
{
    public static IReadOnlyList<(string Name, string Include)> All { get; } =
    [
        ("Microsoft 365", "spf.protection.outlook.com"),
        ("Google Workspace", "_spf.google.com"),
        ("Zoho", "zoho.com"),
        ("Zoho (EU)", "zoho.eu"),
        ("Fastmail", "spf.messagingengine.com"),
        ("Proton Mail", "_spf.protonmail.ch"),
        ("Mailchimp", "servers.mcsv.net"),
        ("SendGrid", "sendgrid.net"),
        ("SMTP2GO", "spf.smtp2go.com"),
        ("Amazon SES", "amazonses.com"),
        ("Salesforce", "_spf.salesforce.com"),
    ];

    private static readonly Dictionary<string, string> IncludeByDetectedName = new(StringComparer.Ordinal)
    {
        ["Microsoft 365"] = "spf.protection.outlook.com",
        ["Google Workspace"] = "_spf.google.com",
        ["Zoho Mail"] = "zoho.com",
        ["Zoho"] = "zoho.com",
        ["Fastmail"] = "spf.messagingengine.com",
        ["ProtonMail"] = "_spf.protonmail.ch",
        ["Mailchimp"] = "servers.mcsv.net",
        ["SendGrid"] = "sendgrid.net",
        ["SMTP2GO"] = "spf.smtp2go.com",
        ["Amazon SES"] = "amazonses.com",
        ["Salesforce"] = "_spf.salesforce.com",
    };

    /// <summary>The includes for the services found in DNS, and for the recognised services (DotMarc.Senders.KnownSenders
    /// names) that the domain's reports show sending its mail.</summary>
    public static IReadOnlyList<string> SuggestedIncludes(IEnumerable<DetectedMailService> detected, IEnumerable<string>? reportedSenders = null) =>
        detected.Select(service => service.ProviderName).Concat(reportedSenders ?? [])
            .Select(name => IncludeByDetectedName.GetValueOrDefault(name)).OfType<string>().Distinct().ToList();
}
