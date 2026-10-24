using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotMarc.Lookalikes;

/// <summary>A lookalike that's registered, and whether it can receive mail.</summary>
public sealed record RegisteredLookalike(string Name, string Kind, bool HasMx);

public interface ILookalikeChecker
{
    Task<IReadOnlyList<RegisteredLookalike>> FindRegisteredAsync(IReadOnlyList<LookalikeCandidate> candidates, CancellationToken cancellationToken);
}

/// <summary>Finds which lookalikes are registered, over DNS-over-HTTPS like the other checkers: a registered domain has
/// nameservers. For those it also looks for MX records (a null MX, "0 .", doesn't count).</summary>
public sealed class LookalikeChecker(HttpClient http) : ILookalikeChecker
{
    private const int Parallelism = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<RegisteredLookalike>> FindRegisteredAsync(IReadOnlyList<LookalikeCandidate> candidates, CancellationToken cancellationToken)
    {
        var registered = new System.Collections.Concurrent.ConcurrentBag<RegisteredLookalike>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (candidate, token) =>
        {
            var nameservers = await QueryAsync(candidate.Name, "NS", token).ConfigureAwait(false);
            if (!nameservers.Any(answer => answer.Type == 2))
            {
                return;
            }

            var mx = await QueryAsync(candidate.Name, "MX", token).ConfigureAwait(false);
            var hasMx = mx.Any(answer => answer.Type == 15 && !answer.Data.Trim().EndsWith(" .", StringComparison.Ordinal));
            registered.Add(new RegisteredLookalike(candidate.Name, candidate.Kind, hasMx));
        }).ConfigureAwait(false);

        return registered.OrderBy(lookalike => lookalike.Name, StringComparer.Ordinal).ToList();
    }

    private async Task<List<DnsAnswer>> QueryAsync(string name, string type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type={type}");
        request.Headers.Accept.ParseAdd("application/dns-json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), JsonOptions);
        return parsed?.Answer ?? [];
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
