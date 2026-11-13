namespace DotMarc.Senders;

/// <summary>Singleton row (seeded <c>Id = 1</c>) for optional source suggestions. The API key lives in the secret
/// store under <see cref="ApiKeySecretKey"/>, never on this entity. Suggestions stay off until an admin turns them
/// on and stores an endpoint, a model name and a key. After that, every signed-in app user can ask.</summary>
public sealed class SourceSuggestionSettings
{
    public const string ApiKeySecretKey = "SourceSuggestions.ApiKey";

    public int Id { get; set; }

    /// <summary>Admin switch. Off means nobody can ask for a suggestion.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL, such as https://api.openai.com/v1, or the full chat-completions URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model name the provider expects.</summary>
    public string? Model { get; set; }

    public bool ApiKeyConfigured { get; set; }

    /// <summary>True when an admin has turned suggestions on and stored everything a call needs.</summary>
    public bool IsReady() =>
        Enabled && !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model) && ApiKeyConfigured;

    /// <summary>The Suggest button. Shown when a provider is connected and this person is a signed-in app user.
    /// The demo never counts as connected. Client portal users are not app users for this.</summary>
    public static bool ShowSuggest(bool providerReady, bool signedInAppUser) =>
        providerReady && signedInAppUser;
}
