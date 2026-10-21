using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Dns;

public interface ICnameLookup
{
    /// <summary>The CNAME target of a name, or null when it has none (or the lookup fails).</summary>
    Task<string?> LookupAsync(string name, CancellationToken cancellationToken);
}

/// <summary>Resolves one name's CNAME over DNS-over-HTTPS, to show where a return-path subdomain points. Its own small
/// caller, as with this codebase's other DNS lookups.</summary>
public sealed class CnameLookup(HttpClient http) : ICnameLookup
{
    private const int CnameType = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string?> LookupAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=CNAME");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var parsed = await JsonSerializer.DeserializeAsync<DnsOverHttpsResponse>(
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), JsonOptions, cancellationToken).ConfigureAwait(false);
            return parsed?.Answer?.FirstOrDefault(answer => answer.Type == CnameType)?.Data.TrimEnd('.');
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
