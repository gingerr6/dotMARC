using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps CertificateCheckStatus to the colour and label on DomainDetail.razor's Domain health checklist.</summary>
public static class CertificateStatusPresentation
{
    public static Color GetColor(CertificateCheckStatus status) => status switch
    {
        CertificateCheckStatus.Ok => Color.Success,
        CertificateCheckStatus.ExpiringSoon or CertificateCheckStatus.Invalid => Color.Warning,
        CertificateCheckStatus.Expired => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(CertificateCheckStatus status) => status switch
    {
        CertificateCheckStatus.Ok => "OK",
        CertificateCheckStatus.NoWebsite => "No HTTPS site",
        CertificateCheckStatus.ExpiringSoon => "Expiring soon",
        CertificateCheckStatus.Expired => "Expired",
        CertificateCheckStatus.Invalid => "Not valid",
        _ => "Not checked yet"
    };
}
