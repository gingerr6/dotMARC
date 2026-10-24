using System.Net;
using System.Net.Sockets;
using DotMarc.Data;
using DotMarc.MtaSts;

namespace DotMarc.Dns;

public sealed record BlocklistResult(BlocklistStatus Status, string? Detail);

/// <summary>One spam blocklist, queried over DNS (DNSBL): an address is listed when its reversed octets under the zone
/// resolve to 127.0.0.x.</summary>
public sealed record Blocklist(string Name, string Zone);

/// <summary>Answers DNSBL queries. The system resolver is used, not DNS-over-HTTPS: the lists refuse queries arriving
/// through big public resolvers such as Cloudflare's.</summary>
public interface IDnsblResolver
{
    /// <summary>The addresses the name resolves to, or none when it doesn't exist.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken);
}

public sealed class SystemDnsblResolver : IDnsblResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await System.Net.Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return [];
        }
    }
}

public interface IBlocklistChecker
{
    Task<BlocklistResult> CheckAsync(string domainName, IReadOnlyList<string> approvedIps, CancellationToken cancellationToken);
}

/// <summary>Checks the domain's own sending servers against the main spam blocklists: the single addresses in its SPF
/// record, IP senders approved on its Sources tab, and its own mail servers (MX). Big providers' shared servers (Microsoft
/// 365, Google, Mimecast and so on) are left out: being listed there is the provider's to fix, not the domain owner's.</summary>
public sealed class BlocklistChecker(ITxtRecordLookup txtLookup, IMxHostsLookup mxHostsLookup, IDnsblResolver resolver) : IBlocklistChecker
{
    public static IReadOnlyList<Blocklist> Lists { get; } =
    [
        new("Spamhaus ZEN", "zen.spamhaus.org"),
        new("SpamCop", "bl.spamcop.net"),
        new("Barracuda", "b.barracudacentral.org"),
        new("PSBL", "psbl.surriel.com"),
        new("Mailspike", "bl.mailspike.net"),
        new("UCEPROTECT L1", "dnsbl-1.uceprotect.net"),
    ];

    /// <summary>Mail hosts run by providers, whose shared servers aren't the domain owner's to fix.</summary>
    private static readonly string[] ProviderHosts =
        ["outlook.com", "google.com", "googlemail.com", "mimecast.com", "pphosted.com", "ppe-hosted.com", "messagelabs.com", "barracudanetworks.com", "mailcontrol.com", "icloud.com", "zoho.com", "zoho.eu", "secureserver.net"];

    private const int MaxAddresses = 20;

    public async Task<BlocklistResult> CheckAsync(string domainName, IReadOnlyList<string> approvedIps, CancellationToken cancellationToken)
    {
        var targets = await TargetsAsync(domainName, approvedIps, cancellationToken).ConfigureAwait(false);
        if (targets.Count == 0)
        {
            return new BlocklistResult(BlocklistStatus.NothingToCheck,
                "No sending servers of the domain's own to check: no single addresses in its SPF record, no approved IP senders, and its mail servers are a provider's.");
        }

        var listings = new List<string>();
        var unavailable = new HashSet<string>();
        foreach (var (address, source) in targets)
        {
            var lists = new List<string>();
            foreach (var list in Lists)
            {
                var answers = await resolver.ResolveAsync($"{Reverse(address)}.{list.Zone}", cancellationToken).ConfigureAwait(false);
                switch (Classify(answers))
                {
                    case Listing.Listed: lists.Add(list.Name); break;
                    case Listing.Refused: unavailable.Add(list.Name); break;
                }
            }
            if (lists.Count > 0)
            {
                listings.Add($"{address} ({source}) on {string.Join(", ", lists)}");
            }
        }

        var checkedNote = $"Checked {targets.Count} address(es) against {Lists.Count - unavailable.Count} blocklists.";
        var unavailableNote = unavailable.Count == 0 ? "" : $" {string.Join(", ", unavailable)} refused this server's queries, so couldn't be checked.";
        return listings.Count > 0
            ? new BlocklistResult(BlocklistStatus.Listed, $"Listed: {string.Join("; ", listings)}. Mail from a listed server is often junked or refused. Find out why with the list's own lookup, fix the cause, then ask for removal.{unavailableNote}")
            : new BlocklistResult(BlocklistStatus.Clear, checkedNote + unavailableNote);
    }

    private enum Listing { NotListed, Listed, Refused }

    /// <summary>127.0.0.x is a listing. Spamhaus answers 127.255.255.x when it refuses the query (a public resolver, or
    /// too many queries), which says nothing about the address.</summary>
    private static Listing Classify(IReadOnlyList<IPAddress> answers)
    {
        var bytes = answers.Select(answer => answer.GetAddressBytes()).Where(b => b.Length == 4 && b[0] == 127).ToList();
        return bytes.Any(b => b[1] == 255 && b[2] == 255) ? Listing.Refused
            : bytes.Any(b => b[1] == 0 && b[2] == 0) ? Listing.Listed
            : Listing.NotListed;
    }

    private async Task<List<(string Address, string Source)>> TargetsAsync(string domainName, IReadOnlyList<string> approvedIps, CancellationToken cancellationToken)
    {
        var targets = new Dictionary<string, string>();
        void Add(string address, string source)
        {
            if (IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && PublicHttpFetcher.IsPublic(ip) && targets.Count < MaxAddresses)
            {
                targets.TryAdd(ip.ToString(), source);
            }
        }

        foreach (var txt in await txtLookup.GetTxtValuesAsync(domainName, cancellationToken).ConfigureAwait(false))
        {
            if (!SpfRecord.IsSpf(txt))
            {
                continue;
            }
            foreach (var term in SpfRecord.Parse(txt).Terms.Where(term => term.Name.Equals("ip4", StringComparison.OrdinalIgnoreCase)))
            {
                var value = term.Argument.TrimStart(':');
                if (value.EndsWith("/32", StringComparison.Ordinal))
                {
                    value = value[..^3];
                }
                if (!value.Contains('/'))
                {
                    Add(value, "in SPF");
                }
            }
        }

        foreach (var ip in approvedIps)
        {
            Add(ip, "approved sender");
        }

        foreach (var host in await mxHostsLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false))
        {
            if (ProviderHosts.Any(provider => host.Equals(provider, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + provider, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            foreach (var address in await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false))
            {
                Add(address.ToString(), $"mail server {host}");
            }
        }

        return targets.Select(pair => (pair.Key, pair.Value)).ToList();
    }

    private static string Reverse(string address) => string.Join('.', address.Split('.').Reverse());
}
