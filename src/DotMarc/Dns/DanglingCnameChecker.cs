using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Data;

namespace DotMarc.Dns;

public sealed record DanglingCnameResult(DanglingCnameStatus Status, string? Detail);

public interface IDanglingCnameChecker
{
    Task<DanglingCnameResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}

/// <summary>Looks for subdomain takeover risks: a common subdomain that is a CNAME to a name that no longer exists
/// (NXDOMAIN), typically a cloud app, storage bucket or SaaS site that was deleted while its DNS record was left
/// behind. Whoever claims that name then controls the subdomain. dotMARC can't list a zone's records, so it checks
/// the subdomains such records usually sit on. A target that still exists but is unclaimed at its provider (a
/// deleted GitHub Pages site, say) needs that provider's own fingerprint and isn't caught here.</summary>
public sealed class DanglingCnameChecker(HttpClient http) : IDanglingCnameChecker
{
    /// <summary>The subdomains checked: Microsoft 365's own records, and the ones most often pointed at a hosted
    /// service.</summary>
    public static IReadOnlyList<string> Labels { get; } =
    [
        "www", "mail", "webmail", "email", "autodiscover", "enterpriseenrollment", "enterpriseregistration", "lyncdiscover",
        "sip", "msoid", "owa", "remote", "vpn", "portal", "shop", "store", "blog", "news", "app", "api", "cdn", "static",
        "assets", "images", "files", "docs", "help", "support", "status", "dev", "test", "staging", "beta", "mta-sts",
    ];

    private const int MaxChainLength = 8;
    private const int NxDomain = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DanglingCnameResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        var dangling = new List<string>();
        var cnames = 0;
        foreach (var label in Labels)
        {
            var name = $"{label}.{domainName}";
            var target = await CnameTargetAsync(name, cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                continue;
            }

            cnames++;

            // Follow the chain to its end: the last name in it is the one that has to exist.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name, target };
            for (var hop = 0; hop < MaxChainLength && await CnameTargetAsync(target, cancellationToken).ConfigureAwait(false) is { } next && seen.Add(next); hop++)
            {
                target = next;
            }

            if (await StatusAsync(target, cancellationToken).ConfigureAwait(false) == NxDomain)
            {
                dangling.Add($"{name} → {target}");
            }
        }

        if (dangling.Count > 0)
        {
            return new DanglingCnameResult(DanglingCnameStatus.Dangling,
                $"Points at a name that no longer exists, so whoever claims it controls the subdomain: {string.Join("; ", dangling)}. Remove the record, or point it somewhere that's yours.");
        }

        return new DanglingCnameResult(DanglingCnameStatus.Ok,
            cnames == 0 ? $"No CNAME records on the {Labels.Count} common subdomains checked." : $"{cnames} CNAME record(s) checked; every target exists.");
    }

    private async Task<string?> CnameTargetAsync(string name, CancellationToken cancellationToken)
    {
        var response = await QueryAsync(name, "CNAME", cancellationToken).ConfigureAwait(false);
        var data = (response.Answer ?? []).FirstOrDefault(answer => answer.Type == 5)?.Data.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(data) ? null : data;
    }

    private async Task<int> StatusAsync(string name, CancellationToken cancellationToken) =>
        (await QueryAsync(name, "A", cancellationToken).ConfigureAwait(false)).Status;

    private async Task<DnsOverHttpsResponse> QueryAsync(string name, string type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Status")] int Status, [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
