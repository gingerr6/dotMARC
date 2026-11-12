namespace DotMarc.Data;

/// <summary>Every independently-grantable capability in the app. A fixed, closed set - adding a
/// new one is a code change (a new UI surface to gate), not something an admin can define, so this
/// is an enum rather than a database-driven list.</summary>
public enum Permission
{
    DomainsView,
    DomainsAdd,
    DomainsEdit,
    DomainsReorder,
    DomainsDelete,
    GroupsView,
    GroupsAdd,
    GroupsRename,
    GroupsDelete,
    TagsView,
    TagsAdd,
    TagsEdit,
    TagsDelete,
    AccessManage,
    MtaStsView,
    MtaStsManage,
    AlertsView,
    AlertsManage,
    DnsPushManage,
    LogsView,
    AuditView,
    AuditManage,
    ReportsManage,
    ServerManage,

    /// <summary>Push records to a domain's DNS provider from dotMARC. Without it, dotMARC shows the records to copy.</summary>
    DnsPush,

    /// <summary>Ask the configured model for a suggestion about a source. Off unless a role includes it. The person's
    /// own switch on the Sources tab still has to be on before the Suggest button is shown. Admin has it because Admin
    /// has every permission; Viewer does not.</summary>
    SourceSuggestions
}
