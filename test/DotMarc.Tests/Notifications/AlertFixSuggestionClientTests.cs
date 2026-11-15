using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Senders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertFixSuggestionClientTests
{
    private static readonly AlertEvent SpfAlert = new()
    {
        Id = 7,
        DomainName = "contoso.com",
        AlertType = AlertTypes.SpfRecordBroken,
        Severity = "Warning",
        Title = "SPF record broken on contoso.com",
        Message = "contoso.com publishes two SPF records.",
    };

    private static readonly string[] AllowedFactNames =
    [
        "Domain", "AlertType", "AlertName", "AlertDescription", "Severity", "Title", "Message", "IsOpen", "DnsProvider", "DnsChecks",
    ];

    [Fact]
    public void Facts_CarryOnlyTheAlertAndDnsChecks()
    {
        var names = typeof(AlertFixFacts).GetProperties().Select(property => property.Name).Order().ToArray();

        Assert.Equal(AllowedFactNames.Order(), names);
    }

    [Fact]
    public void Facts_NameTheAlertType_AndListOnlyChecksThatRan()
    {
        var domain = new Domain
        {
            Name = "contoso.com",
            SpfCheckStatus = SpfCheckStatus.MultipleRecords,
            SpfCheckedUtc = DateTimeOffset.UtcNow,
            SpfCheckDetail = "Two  SPF\nrecords found.",
            DmarcPolicy = DmarcPolicyLevel.Quarantine,
            DmarcPercent = 50,
            DmarcCheckedUtc = DateTimeOffset.UtcNow,
            DkimSelectors = ["selector1"],
        };

        var facts = AlertFixFacts.From(SpfAlert, domain);

        Assert.Equal("SPF record broken", facts.AlertName);
        Assert.True(facts.IsOpen);
        Assert.Contains(facts.DnsChecks, check => check.StartsWith("SPF: MultipleRecords. Two SPF records found.", StringComparison.Ordinal));
        Assert.Contains(facts.DnsChecks, check => check.StartsWith("DMARC: ", StringComparison.Ordinal) && check.Contains("p=quarantine pct=50"));
        // DKIM, MX and the rest have never been checked, so there's nothing to say about them.
        Assert.DoesNotContain(facts.DnsChecks, check => check.StartsWith("DKIM", StringComparison.Ordinal));
        Assert.DoesNotContain(facts.DnsChecks, check => check.StartsWith("MX", StringComparison.Ordinal));
    }

    [Fact]
    public void Facts_CutALongCheckDetail()
    {
        var domain = new Domain
        {
            Name = "contoso.com",
            BlocklistCheckedUtc = DateTimeOffset.UtcNow,
            BlocklistDetail = string.Join(' ', Enumerable.Repeat("listed", 400)),
        };

        var check = Assert.Single(AlertFixFacts.From(SpfAlert, domain).DnsChecks);

        Assert.EndsWith("…", check);
        Assert.True(check.Length < AlertFixFacts.MaximumDetailLength + 40);
    }

    [Fact]
    public void Facts_ForAnAlertWithNoDomainRecord_StillCarryTheAlert()
    {
        var alert = new AlertEvent { DomainName = "dotMARC", AlertType = AlertTypes.BackupFailed, Severity = "Warning", Title = "Backup failed", Message = "S3 refused the upload." };

        var facts = AlertFixFacts.From(alert, domain: null);

        Assert.Equal("Backup failed", facts.AlertName);
        Assert.Empty(facts.DnsChecks);
        Assert.Null(facts.DnsProvider);
    }

    [Fact]
    public async Task SuggestFix_SendsTheAlert_AndReadsTheSteps()
    {
        string? systemMessage = null;
        string? userMessage = null;
        string? authorization = null;
        var handler = new StubHandler(async request =>
        {
            authorization = request.Headers.Authorization?.ToString();
            var document = await request.Content!.ReadFromJsonAsync<JsonElement>();
            var messages = document.GetProperty("messages").EnumerateArray().ToList();
            systemMessage = messages.Single(message => message.GetProperty("role").GetString() == "system").GetProperty("content").GetString();
            userMessage = messages.Single(message => message.GetProperty("role").GetString() == "user").GetProperty("content").GetString();
            var reply = JsonSerializer.Serialize(new
            {
                summary = "contoso.com has two SPF records; merge them into one.",
                steps = new[] { "List the TXT records at contoso.com that start with v=spf1.", "Merge their mechanisms into one record and delete the other." },
            });
            return Json(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content = "```json\n" + reply + "\n```" } } } }));
        });
        var client = NewClient(handler);

        var result = await client.SuggestFixAsync("https://models.example/v1", "test-model", "secret-key", AlertFixFacts.From(SpfAlert, null), CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("contoso.com has two SPF records; merge them into one.", result.Summary);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("Bearer secret-key", authorization);
        Assert.Equal(AlertFixInstructions.Text, systemMessage);
        Assert.Contains("Never invent values", systemMessage);
        Assert.Contains("SpfRecordBroken", userMessage);
        Assert.Contains("two SPF records", userMessage);
        Assert.DoesNotContain("secret-key", userMessage);
    }

    [Fact]
    public async Task SuggestFix_ReportsARefusedKey_WithoutTheResponseBody()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("secret-key leaked in the body"),
        }));

        var result = await NewClient(handler).SuggestFixAsync("https://models.example/v1", "m", "secret-key", AlertFixFacts.From(SpfAlert, null), CancellationToken.None);

        Assert.Contains("refused the API key", result.Error);
        Assert.DoesNotContain("leaked", result.Error);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task SuggestFix_TimesOut_WithoutThrowing()
    {
        var handler = new StubHandler(_ => Task.FromException<HttpResponseMessage>(new TaskCanceledException("timed out")));

        var result = await NewClient(handler).SuggestFixAsync("https://models.example/v1", "m", "k", AlertFixFacts.From(SpfAlert, null), CancellationToken.None);

        Assert.Contains("timed out", result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("I think you should fix SPF.")]
    [InlineData("{\"summary\":\"\",\"steps\":[]}")]
    [InlineData("{\"summary\":[1,2]}")]
    public void Parse_RejectsAReplyWithNothingUsable(string content) =>
        Assert.NotNull(AlertFixReply.Parse(content).Error);

    [Fact]
    public void Parse_KeepsAtMostEightSteps_FlattensLines_AndCapsLongOnes()
    {
        var steps = Enumerable.Range(1, 12).Select(i => $"Step {i}\nwith a break.").ToList();
        steps[0] = string.Join(' ', Enumerable.Repeat("word", 300)) + " UNIQUE_TAIL";

        var result = AlertFixReply.Parse(JsonSerializer.Serialize(new { summary = "Fix it.", steps }));

        Assert.Null(result.Error);
        Assert.Equal(AlertFixReply.MaximumSteps, result.Steps.Count);
        Assert.Equal("Step 2 with a break.", result.Steps[1]);
        Assert.EndsWith("…", result.Steps[0]);
        Assert.DoesNotContain("UNIQUE_TAIL", result.Steps[0]);
    }

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
