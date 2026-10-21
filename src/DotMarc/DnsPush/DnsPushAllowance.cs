using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.DnsPush;

/// <summary>Whether dotMARC may push records for a domain at all, whoever is asking: not when any of its Groups manages
/// its own DNS (Group.ClientManagesDns). Who may push is the DnsPush permission, checked separately.</summary>
public static class DnsPushAllowance
{
    public static Task<bool> IsAllowedAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default) =>
        context.Domains.AsNoTracking().AnyAsync(domain => domain.Id == domainId && !domain.Groups.Any(group => group.ClientManagesDns), cancellationToken);
}
