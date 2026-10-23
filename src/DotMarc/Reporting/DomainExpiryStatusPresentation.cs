using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps DomainExpiryStatus to the colour and label on DomainDetail.razor's Domain health checklist.</summary>
public static class DomainExpiryStatusPresentation
{
    public static Color GetColor(DomainExpiryStatus status) => status switch
    {
        DomainExpiryStatus.Ok => Color.Success,
        DomainExpiryStatus.ExpiringSoon => Color.Warning,
        DomainExpiryStatus.Expired => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(DomainExpiryStatus status) => status switch
    {
        DomainExpiryStatus.Ok => "OK",
        DomainExpiryStatus.NotFound => "No registration",
        DomainExpiryStatus.ExpiringSoon => "Expiring soon",
        DomainExpiryStatus.Expired => "Expired",
        _ => "Not checked yet"
    };
}
