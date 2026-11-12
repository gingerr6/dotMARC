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

    /// <summary>A reply longer than this is cut on a word, so a runaway answer can't fill the row. A normal one-sentence
    /// reason is far shorter and is kept whole.</summary>
    internal const int MaximumReasonLength = 1000;

    /// <summary>Sent as the system message. One concise sentence, and any other domain in the auth results is named
    /// for what it is (the envelope domain, or the domain that signed) so it isn't read as the domain on the page.</summary>
    internal const string Instructions =
        "You classify one sending source from a DMARC aggregate report for the domain in the domain field (the From domain on the page). " +
        "You are given only that domain, the IP, reverse DNS name, network owner, country, message count, SPF and DKIM results, " +
        "disposition, and any classification the app already made. You are not given message content. " +
        "Reply with JSON only, no markdown, in this shape: " +
        "{\"verdict\":\"legitimate_service\"|\"forwarder\"|\"likely_spoof\"|\"unknown\",\"reason\":\"one sentence\"}. " +
        "The reason is one concise sentence and nothing more. " +
        "legitimate_service means a real mail service the domain might send through. forwarder means the mail was passed on " +
        "(a forward, an alias, or a mailing list). likely_spoof means the mail was probably forged. unknown means you cannot tell. " +
        "If you mention a domain from the auth results that is not the domain field, say what it is. " +
        "An SPF domain other than the domain field is the envelope (bounce) domain the message was sent from, not the From domain this page is about. " +
        "A DKIM domain other than the domain field is the domain that signed the message. " +
        "Do not leave the reader thinking that other domain is the one they are viewing. " +
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
                new ChatMessage("system", Instructions),
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
        return SourceSuggestionResult.Ok(verdict, LimitReason(reason));
    }

    /// <summary>Keeps a normal sentence whole. Past <see cref="MaximumReasonLength"/>, cuts on a word when there is one
    /// near the end, then adds an ellipsis.</summary>
    internal static string LimitReason(string reason)
    {
        if (reason.Length == 0)
        {
            return "No reason given.";
        }

        if (reason.Length <= MaximumReasonLength)
        {
            return reason;
        }

        var cut = reason[..MaximumReasonLength];
        var lastSpace = cut.LastIndexOf(' ');
        if (lastSpace >= MaximumReasonLength - 40)
        {
            cut = cut[..lastSpace];
        }

        return cut.TrimEnd() + "…";
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
