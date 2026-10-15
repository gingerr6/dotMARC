namespace DotMarc.Data;

/// <summary>What a person or API key limited to Groups may do. Everything here acts on one domain at a time, so it can
/// be checked against the domain's Groups. Every other permission changes something the whole tenant shares (another
/// client's Groups, tags, alert and PSA settings, DNS provider credentials, who has access, the logs and audit log), so
/// a limited role can't hold it, and AccessClaims drops it for a limited grant whatever the role says.</summary>
public static class GroupLimits
{
    public static readonly IReadOnlySet<Permission> AllowedPermissions = new HashSet<Permission>
    {
        Permission.DomainsView,
        Permission.DomainsAdd,
        Permission.DomainsEdit,
        Permission.DomainsDelete,
        Permission.GroupsView,
        Permission.TagsView,
        Permission.AlertsView,
        Permission.MtaStsView,
        Permission.MtaStsManage,
    };

    public static bool IsAllowed(Permission permission) => AllowedPermissions.Contains(permission);

    public static bool AllAllowed(IEnumerable<Permission> permissions) => permissions.All(IsAllowed);
}
