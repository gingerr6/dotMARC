namespace DotMarc.Data;

/// <summary>The result of the most recent lookalike domain check for a Domain - see DotMarc.Lookalikes.LookalikeService.
/// NotChecked is listed first so it is the enum's (and the database column's) default value.</summary>
public enum LookalikeStatus
{
    NotChecked,

    /// <summary>A subdomain, or a name too short to vary: there are no typo variants worth checking.</summary>
    NotApplicable,

    /// <summary>No lookalike that can receive mail, or only ones someone has dismissed.</summary>
    Clear,

    /// <summary>A registered lookalike that can receive mail and nobody has dismissed: the usual set-up for invoice fraud.</summary>
    Found
}
