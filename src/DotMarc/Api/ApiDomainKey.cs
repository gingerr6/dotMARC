using System.Globalization;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

/// <summary>How the API's /domains/{domain} routes find a domain: by its id, or by its name, so a script that knows the
/// name needn't look the id up first. Names are unique, and a domain name can never be all digits (no top-level domain
/// is numeric), so digits always mean an id.</summary>
public static class ApiDomainKey
{
    public const string RouteParameter = "{domain}";

    public const string Description = "Accepts the domain's id or its name, such as contoso.example.";

    public static IQueryable<Domain> Matching(IQueryable<Domain> domains, string key)
    {
        if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return domains.Where(domain => domain.Id == id);
        }

        // The same normalising the app does when a domain is added: case, a trailing dot, international names.
        return DomainNameValidator.TryNormalize(key, out var name)
            ? domains.Where(domain => domain.Name == name)
            : domains.Where(_ => false);
    }

    /// <summary>Several keys at once, in one query. <see cref="ResolvedDomains.MissingKey"/> is the first key that matched
    /// nothing (including one that is neither an id nor a domain name), so a filter can 404 on it as a route would.</summary>
    public static async Task<ResolvedDomains> ResolveAsync(IQueryable<Domain> domains, IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        var ids = new List<int>();
        var names = new List<string>();
        foreach (var key in keys)
        {
            if (int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
            else if (DomainNameValidator.TryNormalize(key, out var name))
            {
                names.Add(name);
            }
        }

        var found = await domains
            .Where(domain => ids.Contains(domain.Id) || names.Contains(domain.Name))
            .Select(domain => new ResolvedDomain(domain.Id, domain.Name))
            .ToListAsync(cancellationToken);
        var missing = keys.FirstOrDefault(key =>
            int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                ? found.All(domain => domain.Id != id)
                : !DomainNameValidator.TryNormalize(key, out var name) || found.All(domain => domain.Name != name));
        return new ResolvedDomains(found, missing);
    }
}

public sealed record ResolvedDomain(int Id, string Name);

public sealed record ResolvedDomains(IReadOnlyList<ResolvedDomain> Domains, string? MissingKey);
