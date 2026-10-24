using DotMarc.Data;
using MudBlazor;

namespace DotMarc.Reporting;

/// <summary>Maps BlocklistStatus to the colour and label on DomainDetail.razor's Domain health checklist.</summary>
public static class BlocklistStatusPresentation
{
    public static Color GetColor(BlocklistStatus status) => status switch
    {
        BlocklistStatus.Clear => Color.Success,
        BlocklistStatus.Listed => Color.Error,
        _ => Color.Default
    };

    public static string GetLabel(BlocklistStatus status) => status switch
    {
        BlocklistStatus.Clear => "Not listed",
        BlocklistStatus.Listed => "Listed",
        BlocklistStatus.NothingToCheck => "Nothing to check",
        _ => "Not checked yet"
    };
}
