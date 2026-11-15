using System.Text.Json;
using System.Text.Json.Serialization;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Senders;

namespace DotMarc.Notifications;

/// <summary>Asks an OpenAI-compatible chat endpoint how to fix one alert. Implementations send only
/// <see cref="AlertFixFacts"/> and change nothing: no DNS push, no acknowledgement, no ticket.</summary>
public interface IAlertFixClient
{
    Task<AlertFixResult> SuggestFixAsync(string endpoint, string model, string apiKey, AlertFixFacts facts, CancellationToken cancellationToken);
}

/// <summary>What is sent to the model about one alert: the alert as the Alerts page shows it, and the latest DNS
/// check results dotMARC holds for its domain. Those are public DNS facts and dotMARC's reading of them. No report
/// contents, no settings, no secrets, no other domains. Adding a field here is adding something that leaves the
/// server.</summary>
public sealed record AlertFixFacts(
    string Domain,
    string AlertType,
    string AlertName,
    string? AlertDescription,
    string Severity,
    string Title,
    string Message,
    bool IsOpen,
    string? DnsProvider,
    IReadOnlyList<string> DnsChecks)
{
    /// <summary>A check's detail longer than this is cut, so one noisy check can't crowd out the alert.</summary>
    internal const int MaximumDetailLength = 500;

    public static AlertFixFacts From(AlertEvent alert, Domain? domain)
    {
        var type = AlertTypes.Find(alert.AlertType);
        return new AlertFixFacts(
            alert.DomainName,
            alert.AlertType,
            type?.DisplayName ?? alert.AlertType,
            type?.Description,
            alert.Severity,
            alert.Title,
            alert.Message,
            !alert.IsResolved,
            domain is null || domain.DnsProvider is DetectedDnsProvider.NotChecked or DetectedDnsProvider.Unknown ? null : domain.DnsProvider.ToString(),
            domain is null ? [] : DnsChecksOf(domain));
    }

    /// <summary>Each DNS check that has run, as "Name: status. detail".</summary>
    internal static List<string> DnsChecksOf(Domain domain)
    {
        var checks = new List<string>();
        void Add(string name, DateTimeOffset? checkedUtc, object status, string? detail)
        {
            if (checkedUtc is null)
            {
                return;
            }

            var text = $"{name}: {status}";
            if (!string.IsNullOrWhiteSpace(detail))
            {
                var trimmed = string.Join(' ', detail.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                text += ". " + (trimmed.Length <= MaximumDetailLength ? trimmed : trimmed[..MaximumDetailLength] + "…");
            }

            checks.Add(text);
        }

        var policy = domain.DmarcPolicy is { } p
            ? $"p={p.ToString().ToLowerInvariant()}{(domain.DmarcSubdomainPolicy is { } sp ? $" sp={sp.ToString().ToLowerInvariant()}" : "")}{(domain.DmarcPercent is { } pct ? $" pct={pct}" : "")}. "
            : "";
        Add("DMARC", domain.DmarcCheckedUtc, domain.DmarcCheckStatus, policy + domain.DmarcCheckDetail);
        Add("DMARC report authorization", domain.DmarcAuthorizationCheckedUtc, domain.DmarcAuthorizationCheckStatus, domain.DmarcAuthorizationCheckDetail);
        Add("SPF", domain.SpfCheckedUtc, domain.SpfCheckStatus, domain.SpfCheckDetail);
        Add(domain.DkimSelectors.Count == 0 ? "DKIM" : $"DKIM (selectors {string.Join(", ", domain.DkimSelectors)})", domain.DkimCheckedUtc, domain.DkimCheckStatus, domain.DkimCheckDetail);
        Add("MX", domain.MxCheckedUtc, domain.MxCheckStatus, domain.MxCheckDetail);
        Add("TLS-RPT", domain.TlsrptCheckedUtc, domain.TlsrptCheckStatus, domain.TlsrptCheckDetail);
        Add("MTA-STS", domain.MtaStsCheckedUtc, $"{domain.MtaStsStatus} (mode {domain.MtaStsMode})", domain.MtaStsCheckDetail);
        Add("Dangling CNAME", domain.DanglingCnameCheckedUtc, domain.DanglingCnameStatus, domain.DanglingCnameDetail);
        Add("Certificate", domain.CertificateCheckedUtc, domain.CertificateStatus, domain.CertificateDetail);
        Add("Domain registration", domain.DomainExpiryCheckedUtc, domain.DomainExpiryStatus, domain.DomainExpiryDetail);
        Add("Blocklists", domain.BlocklistCheckedUtc, domain.BlocklistStatus, domain.BlocklistDetail);
        Add("Lookalike domains", domain.LookalikeCheckedUtc, domain.LookalikeStatus, domain.LookalikeDetail);
        return checks;
    }
}

/// <summary>A one-sentence summary and a few numbered steps, or an error the page can show. Nothing was changed
/// either way, and nothing is saved.</summary>
public sealed record AlertFixResult(string? Summary, IReadOnlyList<string> Steps, string? Error)
{
    public static AlertFixResult Failed(string error) => new(null, [], error);

    public static AlertFixResult Ok(string summary, IReadOnlyList<string> steps) => new(summary, steps, null);
}

/// <summary>The system message for a fix suggestion.</summary>
public static class AlertFixInstructions
{
    public const string Text =
        "You help an IT technician at a managed service provider fix one alert raised by dotMARC, a DMARC and email DNS monitoring app. " +
        "You are given the alert (its type, name, description, severity, title and message), the domain it is about, the DNS host if known, " +
        "and the latest DNS check results dotMARC holds for that domain. You are not given email content. " +
        "Reply with JSON only, no markdown, in this shape: {\"summary\":\"one sentence\",\"steps\":[\"step\",\"step\"]}. " +
        "The summary says in one sentence what is wrong and what the fix is. " +
        "Give 1 to 6 steps, in order, each one short and concrete: which record, at which host name, and what to change, where the facts support it. " +
        "Never invent values you were not given, such as DKIM public keys, IP addresses, include domains or report addresses: say where to get them instead. " +
        "Keep any DNS record you show valid and in standard syntax. " +
        "If the alert is not fixed in DNS (for example a failed backup, an expiring API key, a lost PSA connection or a failed client report), say what to check instead. " +
        "If the alert reports a change that may have been intended (a weaker DMARC policy, new nameservers), say how to confirm it was intended, " +
        "and that the alert can then be acknowledged in dotMARC; otherwise how to undo it. " +
        "If a step changes how mail is treated (a stricter DMARC policy, removing an SPF include), say to check the DMARC reports first. " +
        "Do not claim you made or will make any change.";
}

/// <summary>Reads the model's reply into an <see cref="AlertFixResult"/>.</summary>
public static class AlertFixReply
{
    internal const int MaximumSteps = 8;
    internal const int MaximumStepLength = 600;
    private const string Unreadable = "The AI provider's reply couldn't be read. Nothing was changed.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static AlertFixResult Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || OpenAiCompatibleSuggestionClient.ExtractJson(content) is not { } json)
        {
            return AlertFixResult.Failed(Unreadable);
        }

        Reply? reply;
        try
        {
            reply = JsonSerializer.Deserialize<Reply>(json, Json);
        }
        catch (JsonException)
        {
            return AlertFixResult.Failed(Unreadable);
        }

        var summary = OneLine(reply?.Summary);
        var steps = (reply?.Steps ?? [])
            .Select(OneLine)
            .Where(step => step.Length > 0)
            .Take(MaximumSteps)
            .Select(step => Limit(step, MaximumStepLength))
            .ToList();
        if (summary.Length == 0 && steps.Count == 0)
        {
            return AlertFixResult.Failed(Unreadable);
        }

        return AlertFixResult.Ok(summary.Length == 0 ? "Suggested steps:" : OpenAiCompatibleSuggestionClient.LimitReason(summary), steps);
    }

    private static string OneLine(string? text) => string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Limit(string text, int maximum)
    {
        if (text.Length <= maximum)
        {
            return text;
        }

        var cut = text[..maximum];
        var lastSpace = cut.LastIndexOf(' ');
        return (lastSpace >= maximum - 40 ? cut[..lastSpace] : cut).TrimEnd() + "…";
    }

    private sealed record Reply(
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("steps")] List<string>? Steps);
}

/// <summary>Asks the configured model how to fix one alert and writes an audit entry. It never acknowledges the
/// alert, pushes DNS or touches a ticket: the person reads the steps and decides.</summary>
public static class AlertFixSuggestionService
{
    public static async Task<AlertFixResult> SuggestAsync(DotMarcDbContext context, ISecretStore secrets, IAlertFixClient client,
        AuditActor actor, AlertEvent alert, Domain? domain, CancellationToken cancellationToken = default)
    {
        if (!await SourceSuggestionAccess.MayAskAsync(context, actor, cancellationToken).ConfigureAwait(false))
        {
            return AlertFixResult.Failed(SourceSuggestionAccess.RefusedMessage);
        }

        var settings = await SourceSuggestionSettingsService.GetAsync(context, cancellationToken).ConfigureAwait(false);
        var apiKey = settings.IsReady()
            ? await secrets.GetSecretAsync(SourceSuggestionSettings.ApiKeySecretKey, cancellationToken).ConfigureAwait(false)
            : null;
        if (string.IsNullOrEmpty(apiKey))
        {
            return AlertFixResult.Failed("AI suggestions aren't configured. Nothing was changed.");
        }

        var facts = AlertFixFacts.From(alert, domain);
        var result = await client.SuggestFixAsync(settings.Endpoint!, settings.Model!, apiKey, facts, cancellationToken).ConfigureAwait(false);
        var summary = result.Error is null
            ? $"Asked for an AI fix suggestion for the {facts.AlertName} alert on {alert.DomainName}: {result.Summary}"
            : $"Asked for an AI fix suggestion for the {facts.AlertName} alert on {alert.DomainName}, and the provider didn't answer.";
        AuditLog.Record(context, actor, AuditActions.AlertFixSuggestionRequested, AuditTarget.For(alert), summary);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
