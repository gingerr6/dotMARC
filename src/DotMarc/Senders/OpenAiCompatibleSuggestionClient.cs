using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DotMarc.Senders;

/// <summary>Asks an OpenAI-compatible chat endpoint about one source. Implementations must send only
/// <see cref="SourceSuggestionFacts"/> and must not change any stored decision.</summary>
public interface ISourceSuggestionClient
{
    Task<SourceSuggestionResult> SuggestAsync(string endpoint, string model, string apiKey, SourceSuggestionFacts facts, CancellationToken cancellationToken);
}

/// <summary>Posts one source to <c>{endpoint}/chat/completions</c> and reads a JSON verdict back. Timeouts and HTTP
/// failures become a result the page can show; they are not thrown, and the response body is never shown to the
/// person (it can be large, and a misconfigured proxy might echo a header).</summary>
public sealed class OpenAiCompatibleSuggestionClient(HttpClient http, ILogger<OpenAiCompatibleSuggestionClient> logger) : ISourceSuggestionClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const string SystemPrompt =
        "You classify one sending source from a DMARC aggregate report. You are given only the IP, reverse DNS name, " +
        "network owner, country, message count, SPF and DKIM results, disposition, and any classification the app already made. " +
        "You are not given message content. Reply with JSON only, no markdown, in this shape: " +
        "{\"verdict\":\"legitimate_service\"|\"forwarder\"|\"likely_spoof\"|\"unknown\",\"reason\":\"one sentence\"}. " +
        "legitimate_service means a real mail service the domain might send through. forwarder means the mail was passed on " +
        "(a forward, an alias, or a mailing list). likely_spoof means the mail was probably forged. unknown means you cannot tell. " +
        "Do not tell the user to change a stored decision; they decide.";

    public async Task<SourceSuggestionResult> SuggestAsync(string endpoint, string model, string apiKey, SourceSuggestionFacts facts, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUri(endpoint));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Content = JsonContent.Create(new ChatRequest(model, 0,
            [
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user", JsonSerializer.Serialize(facts, Json)),
            ]), options: Json);
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SourceSuggestionResult.Failed("The suggestion timed out. Nothing was changed.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Source suggestion request failed before a response.");
            return SourceSuggestionResult.Failed("Couldn't reach the AI provider. Nothing was changed.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Source suggestion provider returned HTTP {StatusCode}.", (int)response.StatusCode);
                var message = (int)response.StatusCode switch
                {
                    401 or 403 => "The AI provider refused the API key. Nothing was changed.",
                    429 => "The AI provider is busy. Try again in a moment. Nothing was changed.",
                    _ => "The AI provider returned an error. Nothing was changed.",
                };
                return SourceSuggestionResult.Failed(message);
            }

            try
            {
                var payload = await response.Content.ReadFromJsonAsync<ChatResponse>(Json, cancellationToken).ConfigureAwait(false);
                var content = payload?.Choices?.FirstOrDefault()?.Message?.Content;
                return Parse(content);
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Source suggestion reply was not JSON.");
                return SourceSuggestionResult.Failed("The AI provider's reply couldn't be read. Nothing was changed.");
            }
        }
    }

    /// <summary>The endpoint may already be the chat-completions URL, or the provider's base (https://host/v1).</summary>
    internal static Uri ChatCompletionsUri(string endpoint)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed, UriKind.Absolute);
        }

        return new Uri(trimmed + "/chat/completions", UriKind.Absolute);
    }

    internal static SourceSuggestionResult Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return SourceSuggestionResult.Failed("The AI provider's reply couldn't be read. Nothing was changed.");
        }

        var json = ExtractJson(content);
        if (json is null)
        {
            return SourceSuggestionResult.Failed("The AI provider's reply couldn't be read. Nothing was changed.");
        }

        VerdictReply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<VerdictReply>(json, Json);
        }
        catch (JsonException)
        {
            return SourceSuggestionResult.Failed("The AI provider's reply couldn't be read. Nothing was changed.");
        }

        if (ParseVerdict(reply?.Verdict) is not { } verdict)
        {
            return SourceSuggestionResult.Failed("The AI provider's reply couldn't be read. Nothing was changed.");
        }

        var reason = string.Join(' ', (reply?.Reason ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (reason.Length > 240)
        {
            reason = reason[..240].TrimEnd() + "…";
        }

        return SourceSuggestionResult.Ok(verdict, reason.Length == 0 ? "No reason given." : reason);
    }

    private static SourceSuggestionVerdict? ParseVerdict(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "legitimate_service" or "legitimate service" => SourceSuggestionVerdict.LegitimateService,
        "forwarder" => SourceSuggestionVerdict.Forwarder,
        "likely_spoof" or "likely spoof" => SourceSuggestionVerdict.LikelySpoof,
        "unknown" => SourceSuggestionVerdict.Unknown,
        _ => null,
    };

    private static string? ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline >= 0)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                trimmed = trimmed[..fence];
            }

            trimmed = trimmed.Trim();
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start < 0 || end <= start ? null : trimmed[start..(end + 1)];
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] int Temperature,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse([property: JsonPropertyName("choices")] List<ChatChoice>? Choices);

    private sealed record ChatChoice([property: JsonPropertyName("message")] ChatMessage? Message);

    private sealed record VerdictReply(
        [property: JsonPropertyName("verdict")] string? Verdict,
        [property: JsonPropertyName("reason")] string? Reason);
}
