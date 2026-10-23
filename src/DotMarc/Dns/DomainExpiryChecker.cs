using System.Net;
using System.Text.Json;
using DotMarc.Data;
using DotMarc.Reporting;

namespace DotMarc.Dns;

public sealed record DomainExpiryResult(DomainExpiryStatus Status, DateTimeOffset? ExpiresUtc, string? Registrar, string? Detail);

public interface IDomainExpiryChecker
{
    Task<DomainExpiryResult> CheckAsync(string domainName, CancellationToken cancellationToken);
}

/// <summary>Looks up when a domain's registration expires, from the registry over RDAP (https://rdap.org/domain/{name},
/// the public bootstrap redirector, which forwards to the registry for the TLD: Nominet for .uk, Verisign for .com and so
/// on). An expired registration takes the domain's mail and website down with it, so a renewal that's been missed is
/// worth knowing about a month ahead.</summary>
public sealed class DomainExpiryChecker(HttpClient http, TimeProvider timeProvider) : IDomainExpiryChecker
{
    /// <summary>Most registrars auto-renew in the last few weeks, so under this it usually needs someone's attention.</summary>
    public const int ExpiringSoonDays = 30;

    public async Task<DomainExpiryResult> CheckAsync(string domainName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"domain/{Uri.EscapeDataString(domainName)}");
        request.Headers.Accept.ParseAdd("application/rdap+json");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new DomainExpiryResult(DomainExpiryStatus.NotFound, null, null,
                $"The registry has no record of {domainName}, so there's no registration to check (a subdomain, or a registry without RDAP).");
        }

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return Evaluate(ExpiryOf(document.RootElement), RegistrarOf(document.RootElement), timeProvider.GetUtcNow());
    }

    public static DomainExpiryResult Evaluate(DateTimeOffset? expiresUtc, string? registrar, DateTimeOffset nowUtc)
    {
        var at = registrar is null ? "" : $" at {registrar}";
        if (expiresUtc is not { } expires)
        {
            return new DomainExpiryResult(DomainExpiryStatus.NotFound, null, registrar, $"The registry doesn't publish an expiry date for this domain{at}.");
        }

        var days = (int)Math.Floor((expires - nowUtc).TotalDays);
        return expires <= nowUtc
            ? new DomainExpiryResult(DomainExpiryStatus.Expired, expires, registrar, $"The registration expired on {UkTime.Date(expires)}. Renew it{at} now: mail and the website stop when the registry suspends it.")
            : days <= ExpiringSoonDays
                ? new DomainExpiryResult(DomainExpiryStatus.ExpiringSoon, expires, registrar, $"The registration expires on {UkTime.Date(expires)}, in {days} days. Check it's set to renew{at}.")
                : new DomainExpiryResult(DomainExpiryStatus.Ok, expires, registrar, $"Registered until {UkTime.Date(expires)}{at}.");
    }

    private static DateTimeOffset? ExpiryOf(JsonElement root)
    {
        if (!root.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in events.EnumerateArray())
        {
            if (item.TryGetProperty("eventAction", out var action) && action.GetString() == "expiration"
                && item.TryGetProperty("eventDate", out var date) && DateTimeOffset.TryParse(date.GetString(), out var parsed))
            {
                return parsed.ToUniversalTime();
            }
        }

        return null;
    }

    /// <summary>The registrar's name, from the entity with the registrar role (its vCard "fn").</summary>
    private static string? RegistrarOf(JsonElement root)
    {
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entity in entities.EnumerateArray())
        {
            var isRegistrar = entity.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                && roles.EnumerateArray().Any(role => role.GetString() == "registrar");
            if (isRegistrar && entity.TryGetProperty("vcardArray", out var vcard) && vcard.ValueKind == JsonValueKind.Array && vcard.GetArrayLength() > 1)
            {
                foreach (var field in vcard[1].EnumerateArray())
                {
                    if (field.ValueKind == JsonValueKind.Array && field.GetArrayLength() > 3 && field[0].GetString() == "fn" && field[3].ValueKind == JsonValueKind.String)
                    {
                        var name = field[3].GetString()?.Trim();
                        return string.IsNullOrEmpty(name) ? null : name;
                    }
                }
            }
        }

        return null;
    }
}
