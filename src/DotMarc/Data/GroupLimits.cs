namespace DotMarc.Data;

/// <summary>What a person or API key limited to Groups may do. Each permission here is checked against the Groups
/// wherever it's used: the domain permissions act on one domain at a time, and the API limits AuditView, ReportsManage
/// and AlertsManage to the key's own domains and Groups. Those three also open tenant-wide pages (the audit log, Email
/// &amp; reports, Alert and PSA settings), which need someone who isn't limited (see the Unlimited* policies). Every
/// other permission changes something the whole tenant shares (other clients' Groups, tags, DNS provider credentials,
/// who has access, the server logs), so a limited role can't hold it, and AccessClaims drops it for a limited grant
/// whatever the role says.</summary>
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
        Permission.AuditView,
        Permission.ReportsManage,
        Permission.DnsPush,
        Permission.AlertsManage,
    };

    public static bool IsAllowed(Permission permission) => AllowedPermissions.Contains(permission);

    public static bool AllAllowed(IEnumerable<Permission> permissions) => permissions.All(IsAllowed);
}
