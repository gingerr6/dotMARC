using System.Globalization;
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Security;

namespace DotMarc.Api;

/// <summary>Which groups a key or a person is limited to. No scoped-group claims means unrestricted. A scoped principal
/// sees a domain when the domain is in at least one of its groups. The app's pages use this too, so a person and a key
/// with the same grant see the same domains.</summary>
public sealed class ApiScope
{
    private readonly int[]? _groupIds;

    private ApiScope(int[]? groupIds) => _groupIds = groupIds;

    public bool IsScoped => _groupIds is not null;

    public static ApiScope From(ClaimsPrincipal principal)
    {
        var groupIds = principal.FindAll(UserAccessClaimsTransformation.ScopedGroupClaimType)
            .Select(claim => int.Parse(claim.Value, CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        return new ApiScope(groupIds.Length == 0 ? null : groupIds);
    }

    public bool Includes(int groupId) => _groupIds is null || _groupIds.Contains(groupId);

    public IQueryable<Domain> Domains(IQueryable<Domain> domains)
    {
        var groupIds = _groupIds;
        return groupIds is null ? domains : domains.Where(domain => domain.Groups.Any(group => groupIds.Contains(group.Id)));
    }

    /// <summary>Alerts name their domain rather than linking to it, so a scoped principal sees an alert when a visible
    /// domain has that name.</summary>
    public IQueryable<AlertEvent> Alerts(IQueryable<AlertEvent> alerts, IQueryable<Domain> domains)
    {
        if (_groupIds is null)
        {
            return alerts;
        }

        var visibleNames = Domains(domains).Select(domain => domain.Name);
        return alerts.Where(alert => visibleNames.Contains(alert.DomainName));
    }

    public IQueryable<Group> Groups(IQueryable<Group> groups)
    {
        var groupIds = _groupIds;
        return groupIds is null ? groups : groups.Where(group => groupIds.Contains(group.Id));
    }
}
