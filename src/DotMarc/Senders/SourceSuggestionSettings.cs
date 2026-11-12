namespace DotMarc.Senders;

/// <summary>Singleton row (seeded <c>Id = 1</c>) for optional source suggestions. The API key lives in the secret
/// store under <see cref="ApiKeySecretKey"/>, never on this entity. Suggestions stay off until an admin turns them
/// on and stores an endpoint, a model name and a key. Each person then chooses whether to use them.</summary>
public sealed class SourceSuggestionSettings
{
    public const string ApiKeySecretKey = "SourceSuggestions.ApiKey";

    public int Id { get; set; }

    /// <summary>Admin switch. Off means nobody can ask for a suggestion, whatever their own preference is.</summary>
    public bool Enabled { get; set; }

    /// <summary>OpenAI-compatible base URL, such as https://api.openai.com/v1, or the full chat-completions URL.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The model name the provider expects.</summary>
    public string? Model { get; set; }

    public bool ApiKeyConfigured { get; set; }

    /// <summary>True when an admin has turned suggestions on and stored everything a call needs.</summary>
    public bool IsReady() =>
        Enabled && !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model) && ApiKeyConfigured;

    /// <summary>The Suggest button is shown only when the provider is ready and this person has turned suggestions on.</summary>
    public static bool ShowSuggest(bool providerReady, bool userEnabled) => providerReady && userEnabled;
}
