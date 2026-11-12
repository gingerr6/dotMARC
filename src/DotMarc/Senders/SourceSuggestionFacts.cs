namespace DotMarc.Senders;

/// <summary>What a suggestion may call a source. The person still approves or rejects it; this never becomes the
/// stored decision on its own.</summary>
public enum SourceSuggestionVerdict
{
    LegitimateService,
    Forwarder,
    LikelySpoof,
    Unknown,
}

public static class SourceSuggestionText
{
    public static string Label(SourceSuggestionVerdict verdict) => verdict switch
    {
        SourceSuggestionVerdict.LegitimateService => "Legitimate service",
        SourceSuggestionVerdict.Forwarder => "Forwarder",
        SourceSuggestionVerdict.LikelySpoof => "Likely spoof",
        _ => "Unknown",
    };
}

/// <summary>A verdict and a one-line reason, or an error the page can show. Nothing was saved either way.</summary>
public sealed record SourceSuggestionResult(SourceSuggestionVerdict? Verdict, string? Reason, string? Error)
{
    public static SourceSuggestionResult Failed(string error) => new(null, null, error);

    public static SourceSuggestionResult Ok(SourceSuggestionVerdict verdict, string reason) => new(verdict, reason, null);
}

/// <summary>The aggregate facts sent to the model. There is no message content: no body, subject, headers or raw
/// report. Adding a field here is adding something that leaves the server, so keep it to what an aggregate report
/// already shows on the Sources tab. <see cref="Domain"/> is the domain the page is about, so the model can tell it
/// apart from an envelope or signing domain in <see cref="AuthResults"/>.</summary>
public sealed record SourceSuggestionFacts(
    string Domain,
    string Ip,
    string? HostName,
    string? Organization,
    string? Country,
    int Volume,
    string Spf,
    string Dkim,
    string Disposition,
    string? RecognisedAs,
    bool KnownForwarder,
    bool LikelySpoof,
    string? SpoofReason,
    string? ForwardingReason,
    string? PolicyOverrides,
    IReadOnlyList<string> AuthResults);
