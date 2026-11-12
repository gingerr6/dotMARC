using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotMarc.Senders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Senders;

public sealed class SourceSuggestionClientTests
{
    private static readonly SourceSuggestionFacts Facts = new(
        "203.0.113.9", "203-0-113-9.hsd1.example.net", "Example ISP", "GB", 4, "Fail", "Fail", "none",
        null, false, true, "Likely spoofing.", null, "Forwarded", ["spf contoso.com: fail"]);

    private static readonly string[] AllowedFactNames =
    [
        "Ip", "HostName", "Organization", "Country", "Volume", "Spf", "Dkim", "Disposition", "RecognisedAs",
        "KnownForwarder", "LikelySpoof", "SpoofReason", "ForwardingReason", "PolicyOverrides", "AuthResults",
    ];

    [Fact]
    public void Facts_CarryOnlyAggregateFields()
    {
        var names = typeof(SourceSuggestionFacts).GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(AllowedFactNames.Order(), names);
        Assert.DoesNotContain(names, name => name.Contains("Body", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Subject", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Raw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShowSuggest_NeedsTheProviderAndThisPersonsSwitch()
    {
        Assert.False(SourceSuggestionSettings.ShowSuggest(providerReady: false, userEnabled: false));
        Assert.False(SourceSuggestionSettings.ShowSuggest(providerReady: false, userEnabled: true));
        Assert.False(SourceSuggestionSettings.ShowSuggest(providerReady: true, userEnabled: false));
        Assert.True(SourceSuggestionSettings.ShowSuggest(providerReady: true, userEnabled: true));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com/v1/chat/completions")]
    [InlineData("https://models.example/v1/chat/completions", "https://models.example/v1/chat/completions")]
    public void ChatCompletionsUri_AppendsThePathOnce(string endpoint, string expected) =>
        Assert.Equal(expected, OpenAiCompatibleSuggestionClient.ChatCompletionsUri(endpoint).AbsoluteUri);

    [Fact]
    public async Task Suggest_SendsTheFactsAndNotTheKey_AndReadsTheVerdict()
    {
        string? userMessage = null;
        string? authorization = null;
        var handler = new StubHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            var document = await request.Content!.ReadFromJsonAsync<JsonElement>();
            userMessage = document.GetProperty("messages").EnumerateArray().Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString();
            return Json("""{"choices":[{"message":{"role":"assistant","content":"```json\n{\"verdict\":\"likely_spoof\",\"reason\":\"Both checks failed from a home connection.\"}\n```"}}]}""");
        });
        var client = NewClient(handler);

        var result = await client.SuggestAsync("https://models.example/v1", "test-model", "secret-key", Facts, CancellationToken.None);

        Assert.Equal(SourceSuggestionVerdict.LikelySpoof, result.Verdict);
        Assert.Equal("Both checks failed from a home connection.", result.Reason);
        Assert.Null(result.Error);
        Assert.Equal("Bearer secret-key", authorization);
        Assert.NotNull(userMessage);
        Assert.Contains("203.0.113.9", userMessage);
        Assert.DoesNotContain("secret-key", userMessage);
        Assert.DoesNotContain("rawXml", userMessage, StringComparison.OrdinalIgnoreCase);
        var facts = JsonSerializer.Deserialize<JsonElement>(userMessage!);
        var sent = facts.EnumerateObject().Select(property => property.Name).ToHashSet();
        Assert.Contains("ip", sent);
        Assert.DoesNotContain("subject", sent);
        Assert.DoesNotContain("body", sent);
    }

    [Fact]
    public async Task Suggest_TimesOut_WithoutThrowing()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));
        var client = NewClient(handler);

        var result = await client.SuggestAsync("https://models.example/v1", "test-model", "secret-key", Facts, CancellationToken.None);

        Assert.Null(result.Verdict);
        Assert.Contains("timed out", result.Error);
    }

    [Fact]
    public async Task Suggest_ReportsARefusedKey_WithoutTheResponseBody()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret-key leaked in the body"),
        }));
        var client = NewClient(handler);

        var result = await client.SuggestAsync("https://models.example/v1", "test-model", "secret-key", Facts, CancellationToken.None);

        Assert.Contains("refused the API key", result.Error);
        Assert.DoesNotContain("secret-key", result.Error);
        Assert.DoesNotContain("leaked", result.Error);
    }

    [Fact]
    public void Parse_RejectsAVerdictItDoesNotKnow() =>
        Assert.NotNull(OpenAiCompatibleSuggestionClient.Parse("{\"verdict\":\"burn it down\",\"reason\":\"no\"}").Error);

    private static OpenAiCompatibleSuggestionClient NewClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), NullLogger<OpenAiCompatibleSuggestionClient>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }
}
