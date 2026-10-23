using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DanglingCnameStatus to the colour and label on DomainDetail.razor's Domain health checklist.</summary>
public static class DanglingCnameStatusPresentation
{
    public static Color GetColor(DanglingCnameStatus status) => status switch
    {
        DanglingCnameStatus.Ok => Color.Success,
        DanglingCnameStatus.Dangling => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DanglingCnameStatus status) => status switch
    {
        DanglingCnameStatus.Ok => "OK",
        DanglingCnameStatus.Dangling => "Takeover risk",
        _ => "Not checked yet"
    };
}
