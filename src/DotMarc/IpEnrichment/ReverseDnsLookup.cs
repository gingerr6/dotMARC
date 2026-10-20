using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.IpEnrichment;

/// <summary>Reverse DNS over the same DNS-over-HTTPS resolver as dotMARC's other DNS checks. Whoever controls an IP's
/// reverse zone can make its PTR record say anything, mail.google.com included, so a name only counts when it resolves
/// back to the same IP (forward-confirmed reverse DNS): the forward zone belongs to whoever owns the name.</summary>
public sealed class ReverseDnsLookup : IReverseDnsLookup
{
    private const int PtrType = 12;
    private const int AType = 1;
    private const int AaaaType = 28;
    private const int MaxNamesChecked = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;

    public ReverseDnsLookup(HttpClient http) => _http = http;

    public async Task<string?> LookupAsync(string ip, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(ip, out var address))
        {
            return null;
        }

        var names = (await QueryAsync(ReverseName(address), "PTR", cancellationToken).ConfigureAwait(false))
            .Where(answer => answer.Type == PtrType)
            .Select(answer => answer.Data.TrimEnd('.').ToLowerInvariant())
            .Where(name => name.Length > 0)
            .Distinct()
            .Take(MaxNamesChecked);

        var forwardType = address.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A";
        var forwardTypeCode = address.AddressFamily == AddressFamily.InterNetworkV6 ? AaaaType : AType;
        foreach (var name in names)
        {
            var forward = await QueryAsync(name, forwardType, cancellationToken).ConfigureAwait(false);
            if (forward.Any(answer => answer.Type == forwardTypeCode && IPAddress.TryParse(answer.Data, out var resolved) && resolved.Equals(address)))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>The in-addr.arpa or ip6.arpa name that holds an IP's PTR record: IPv4 octets reversed, or every IPv6
    /// nibble reversed.</summary>
    public static string ReverseName(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return string.Join('.', bytes.Reverse()) + ".in-addr.arpa";
        }

        var name = new StringBuilder();
        foreach (var b in bytes.Reverse())
        {
            name.Append((b & 0x0F).ToString("x")).Append('.').Append((b >> 4).ToString("x")).Append('.');
        }

        return name.Append("ip6.arpa").ToString();
    }

    private async Task<List<DnsAnswer>> QueryAsync(string name, string type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions);

        // As in TxtRecordLookup: 0 is an answer and 3 (NXDOMAIN) a real "nothing here"; anything else means the resolver
        // couldn't find out, which mustn't be cached as "no name".
        if (parsed is null || parsed.Status is not (0 or 3))
        {
            throw new HttpRequestException($"The DNS resolver couldn't look up {name} (status {parsed?.Status}).");
        }

        return parsed.Answer ?? [];
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
