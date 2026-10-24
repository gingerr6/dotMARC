using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps LookalikeStatus to the colour and label on DomainDetail.razor's Domain health checklist.</summary>
public static class LookalikeStatusPresentation
{
    public static Color GetColor(LookalikeStatus status) => status switch
    {
        LookalikeStatus.Clear => Color.Success,
        LookalikeStatus.Found => Color.Warning,
        _ => Color.Default
    };

    public static string GetLabel(LookalikeStatus status) => status switch
    {
        LookalikeStatus.Clear => "OK",
        LookalikeStatus.Found => "Lookalikes found",
        LookalikeStatus.NotApplicable => "Not applicable",
        _ => "Not checked yet"
    };
}
