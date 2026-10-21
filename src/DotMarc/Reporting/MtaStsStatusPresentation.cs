using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps MtaStsStatus to the MudBlazor color/label pair used on DomainDetail.razor's
/// MTA-STS tab - same shared-presentation-logic precedent as DmarcStatusPresentation.</summary>
public static class MtaStsStatusPresentation
{
    public static Color GetColor(MtaStsStatus status) => status switch
    {
        MtaStsStatus.Active => Color.Success,
        MtaStsStatus.PendingDns or MtaStsStatus.PendingCertificate => Color.Info,
        MtaStsStatus.PendingPolicyRecord => Color.Warning,
        MtaStsStatus.Failed => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(MtaStsStatus status) => status switch
    {
        MtaStsStatus.PendingDns => "Waiting for DNS",
        MtaStsStatus.PendingCertificate => "Provisioning certificate",
        MtaStsStatus.Active => "Active",
        MtaStsStatus.Failed => "Failed",
        MtaStsStatus.PendingPolicyRecord => "Waiting for TXT record",
        _ => "Not configured"
    };

    /// <summary>As GetColor, but an active policy in testing mode shows as not yet protecting: receivers only report
    /// failures, they still deliver over an insecure connection.</summary>
    public static Color GetColor(MtaStsStatus status, MtaStsMode mode) =>
        status == MtaStsStatus.Active && mode != MtaStsMode.Enforce ? Color.Info : GetColor(status);

    /// <summary>As GetLabel, with an active policy's mode: "Active: testing" or "Active: enforcing".</summary>
    public static string GetLabel(MtaStsStatus status, MtaStsMode mode) => status == MtaStsStatus.Active
        ? mode switch
        {
            MtaStsMode.Enforce => "Active: enforcing",
            MtaStsMode.Testing => "Active: testing",
            _ => "Active: withdrawn (none)",
        }
        : GetLabel(status);
}
