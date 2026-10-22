using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using MudBlazor;

namespace DotMarc.Components.Layout;

/// <summary>One page in the Manage section, and the policy that decides who sees it.</summary>
public sealed record ManageItem(string Section, string Href, string Icon, string Title, string Text, string Policy);

/// <summary>The Manage section's pages, in menu order: ManageNav lists them down the left, the Manage overview as tiles.</summary>
public static class ManageItems
{
    public static IReadOnlyList<ManageItem> All { get; } =
    [
        new("Domains", "/domains", Icons.Material.Filled.Language, "Domains", "Add, import, remove and order the domains dotMARC monitors.", "DomainsWrite"),
        new("Domains", "/groups", Icons.Material.Filled.GroupWork, "Groups", "Clients and tags: set up new clients, their DNS checklist, portal users and PSA companies.", "GroupsOrTagsWrite"),
        new("Domains", "/enforcement", Icons.Material.Filled.Shield, "Enforcement", "Which domains are ready to move from p=none to quarantine and reject.", "DomainsView"),
        new("Domains", "/client-reports", Icons.Material.Filled.Assessment, "Client reports", "Schedule, send and download each client's email security report.", "ReportsManage"),
        new("Connections", "/dns-push/settings", Icons.Material.Filled.CloudSync, "DNS push", "Connect Cloudflare, Azure DNS and Google Cloud DNS so records can be pushed.", "DnsPushManage"),
        new("Connections", "/alerts/settings", Icons.Material.Filled.NotificationsActive, "Alerts", "Which alerts are raised, and where they go: email, Teams, Slack.", "UnlimitedAlertsManage"),
        new("Connections", "/psa/settings", Icons.Material.Filled.ConfirmationNumber, "PSA", "Raise alerts as tickets in HaloPSA, ConnectWise, Autotask or NinjaOne.", "UnlimitedAlertsManage"),
        new("Connections", "/reports/settings", Icons.Material.Filled.Email, "Email & reports", "How dotMARC sends email, and the defaults for client reports.", "UnlimitedReportsManage"),
        new("People & brand", "/access", Icons.Material.Filled.ManageAccounts, "Access", "Who can sign in, their roles, the groups they're limited to, and API keys.", "AccessManage"),
        new("People & brand", "/branding/settings", Icons.Material.Filled.Palette, "Branding", "Your logo and colours in the client portal and reports.", "AccessManage"),
        new("Server", "/control-panel", Icons.Material.Filled.Tune, "Control panel", "Offsite backups and updates for this server.", "ServerManage"),
        new("Server", "/logs", Icons.Material.Filled.Article, "Server logs", "What dotMARC has been doing, and any errors.", "LogsView"),
        new("Server", "/audit", Icons.Material.Filled.History, "Audit log", "Every change, sign-in and push, by whom and when.", "UnlimitedAuditView"),
    ];

    /// <summary>The items this user may open.</summary>
    public static async Task<IReadOnlyList<ManageItem>> VisibleToAsync(IAuthorizationService authorization, ClaimsPrincipal user)
    {
        var allowed = new Dictionary<string, bool>();
        var visible = new List<ManageItem>();
        foreach (var item in All)
        {
            if (!allowed.TryGetValue(item.Policy, out var ok))
            {
                ok = allowed[item.Policy] = (await authorization.AuthorizeAsync(user, item.Policy).ConfigureAwait(false)).Succeeded;
            }
            if (ok)
            {
                visible.Add(item);
            }
        }

        return visible;
    }
}
