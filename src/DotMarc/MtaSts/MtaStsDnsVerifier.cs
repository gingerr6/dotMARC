using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.MtaSts;

/// <summary>Confirms a domain's mta-sts.&lt;domain&gt; CNAME actually points at dotMARC's own
/// hosting hostname, querying Cloudflare's DNS-over-HTTPS JSON API - same approach and same
/// reasoning as DotMarc.Dns.DmarcDnsChecker (consistent results independent of the runtime
/// environment's own resolver config, over HTTPS rather than raw UDP:53 so it works reliably from
/// Azure Container Apps).</summary>
public sealed class MtaStsDnsVerifier : IMtaStsDnsVerifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public MtaStsDnsVerifier(HttpClient http) => _http = http;

    public async Task<MtaStsDnsVerificationResult> VerifyAsync(string domainName, string expectedHostingHostname, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"mta-sts.{domainName}")}&type=CNAME");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        var cnameAnswers = parsed.Answer?.Where(a => a.Type == 5).ToList() ?? [];
        if (cnameAnswers.Count == 0)
        {
            return MtaStsDnsVerificationResult.NotFound;
        }

        // A canonical DNS name in the response carries a trailing dot (e.g. "dotmarc.app."); the
        // expected hostname configured for this deployment never does, so it has to be trimmed
        // before comparing.
        var pointsHere = cnameAnswers.Any(a =>
            string.Equals(a.Data.TrimEnd('.'), expectedHostingHostname.TrimEnd('.'), StringComparison.OrdinalIgnoreCase));

        return pointsHere ? MtaStsDnsVerificationResult.Resolved : MtaStsDnsVerificationResult.PointsElsewhere;
    }

    public async Task<MtaStsPolicyRecordCheck> CheckPolicyRecordAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString($"_mta-sts.{domainName}")}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;

        // A TXT answer's data is its quoted strings, which a long record splits; joined, they're the value.
        var values = parsed.Answer?.Where(answer => answer.Type == 16)
            .Select(answer => string.Concat(System.Text.RegularExpressions.Regex.Matches(answer.Data, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(match => match.Groups[1].Value)) is { Length: > 0 } joined ? joined : answer.Data)
            .Where(value => value.TrimStart().StartsWith("v=STSv1", StringComparison.Ordinal))
            .ToList() ?? [];
        return Parse(values);
    }

    /// <summary>RFC 8461 §3.1: exactly one record starting v=STSv1, with an id of 1 to 32 letters and digits.</summary>
    internal static MtaStsPolicyRecordCheck Parse(IReadOnlyList<string> stsValues)
    {
        if (stsValues.Count == 0)
        {
            return new MtaStsPolicyRecordCheck(MtaStsPolicyRecordState.Missing);
        }

        if (stsValues.Count > 1)
        {
            return new MtaStsPolicyRecordCheck(MtaStsPolicyRecordState.Multiple);
        }

        var id = stsValues[0].Split(';').Select(part => part.Trim())
            .Where(part => part.StartsWith("id=", StringComparison.Ordinal))
            .Select(part => part[3..].Trim())
            .FirstOrDefault();
        return id is { Length: >= 1 and <= 32 } && id.All(char.IsAsciiLetterOrDigit)
            ? new MtaStsPolicyRecordCheck(MtaStsPolicyRecordState.Present, id)
            : new MtaStsPolicyRecordCheck(MtaStsPolicyRecordState.Invalid);
    }

    private sealed record DnsOverHttpsResponse(
        [property: JsonPropertyName("Status")] int Status,
        [property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer(
        [property: JsonPropertyName("type")] int Type,
        [property: JsonPropertyName("data")] string Data);
}
