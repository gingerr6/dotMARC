using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Maps a detected provider to the {provider} route segment IDnsPushProvider.ProviderKey
/// values use ("cloudflare"/"azure-dns") - see IDnsPushProvider.ProviderKey's own doc comment for
/// the paired convention this centralizes. Returns null for Unknown/NotChecked, meaning "no
/// configured push provider matches."</summary>
public static class DetectedDnsProviderExtensions
{
    public static string? ToProviderKey(this DetectedDnsProvider provider) => provider switch
    {
        DetectedDnsProvider.Cloudflare => "cloudflare",
        DetectedDnsProvider.AzureDns => "azure-dns",
        DetectedDnsProvider.GoogleCloudDns => "google-cloud-dns",
        _ => null
    };

    /// <summary>Why a push can't go ahead for a domain whose DNS is at this provider, and what to do instead: check it
    /// first, connect its provider, or (for a provider dotMARC can't push to) add the record by hand.</summary>
    public static string NoPushReason(this DetectedDnsProvider provider, string domainName, string what = "the record")
    {
        var label = DotMarc.Reporting.DnsProviderStatusPresentation.GetLabel(provider);
        return provider switch
        {
            DetectedDnsProvider.NotChecked => $"{domainName}'s DNS provider hasn't been checked yet - try the recheck button on the Overview tab, then push again.",
            DetectedDnsProvider.Unknown => $"dotMARC doesn't recognise who hosts {domainName}'s DNS, so it can't push to it. Use Show record and add {what} by hand.",
            _ when provider.ToProviderKey() is null => $"{domainName}'s DNS is at {label}, and dotMARC can only push to Cloudflare, Azure DNS and Google Cloud DNS. Use Show record and add {what} at {label} by hand.",
            _ => $"{domainName}'s DNS is at {label}, which isn't connected to dotMARC yet. Connect it under Manage > DNS push, or use Show record and add {what} by hand.",
        };
    }
}
