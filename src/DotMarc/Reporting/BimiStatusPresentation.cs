using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

public static class BimiStatusPresentation
{
    public static Color GetColor(BimiCheckStatus status) => status switch
    {
        BimiCheckStatus.Ok => Color.Success,
        BimiCheckStatus.LogoOnly => Color.Info,
        BimiCheckStatus.NotEnforced => Color.Warning,
        BimiCheckStatus.Misconfigured => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(BimiCheckStatus status) => status switch
    {
        BimiCheckStatus.Ok => "Logo shown",
        BimiCheckStatus.LogoOnly => "Logo, no certificate",
        BimiCheckStatus.NotEnforced => "Waiting on DMARC",
        BimiCheckStatus.Misconfigured => "Misconfigured",
        BimiCheckStatus.NoRecord => "Not set up",
        _ => "Not checked yet"
    };
}
