using MudBlazor;

namespace DotMarc.Portal;

/// <summary>The colour of a domain's status in the portal. Protected and Needs attention keep green and amber for what
/// they mean; the neutral statuses take the brand's secondary colour.</summary>
public static class PortalHealthColours
{
    public static Color For(PortalHealth health) => health switch
    {
        PortalHealth.Protected => Color.Success,
        PortalHealth.NeedsAttention => Color.Warning,
        _ => Color.Secondary,
    };
}
