namespace DotMarc.Senders;

/// <summary>One person's choices. Missing row means the defaults, and source suggestions default to off. UserKey is
/// the Entra object id when the person has one, otherwise their email, so a later rename of the email doesn't split
/// the preference once they have signed in.</summary>
public sealed class UserPreference
{
    public int Id { get; set; }
    public required string UserKey { get; set; }
    public bool SourceSuggestionsEnabled { get; set; }
}
