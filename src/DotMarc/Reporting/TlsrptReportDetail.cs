using System.Globalization;
using System.Text.Json;
using DotMarc.Data;

namespace DotMarc.Reporting;

/// <summary>One TLS report as the TLS reports tab shows it when a row is opened: each policy the sender applied (a
/// report can carry two when the sender picked up a new version of the policy that day) and each kind of failure.
/// Read from the report's original JSON, which also holds what the stored rows don't: the policy text the sender
/// used (mode, allowed mail servers) and the IPs on each failure. A report whose JSON can't be read falls back to
/// the stored rows.</summary>
public sealed record TlsrptReportDetail(IReadOnlyList<TlsrptPolicyDetail> Policies, bool FromRawJson)
{
    public static TlsrptReportDetail From(TlsrptReport report)
    {
        try
        {
            using var document = JsonDocument.Parse(report.RawJson);
            var policies = document.RootElement.GetProperty("policies").EnumerateArray().Select(ReadPolicy).ToList();
            if (policies.Count > 0)
            {
                return new TlsrptReportDetail(policies, FromRawJson: true);
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Fall back to what was stored when the report was read in.
        }

        return new TlsrptReportDetail(report.Policies.Select(policy => new TlsrptPolicyDetail(
            policy.PolicyType, policy.PolicyDomain, null, [], null, policy.SuccessfulSessionCount, policy.FailedSessionCount,
            policy.FailureDetails.Select(failure => new TlsrptFailureLine(
                failure.ResultType, failure.FailedSessionCount, failure.ReceivingMxHostname, null, null, failure.FailureReasonCode,
                failure.AdditionalInformation)).ToList())).ToList(), FromRawJson: false);
    }

    /// <summary>The original JSON, indented for reading. Unchanged when it isn't valid JSON.</summary>
    public static string Pretty(string rawJson)
    {
        try
        {
            using var document = JsonDocument.Parse(rawJson);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return rawJson;
        }
    }

    /// <summary>"MTA-STS" for sts, "DANE" for tlsa, "No policy" for no-policy-found; anything else as the sender wrote it.</summary>
    public static string PolicyTypeLabel(string policyType) => policyType.Trim().ToLowerInvariant() switch
    {
        "sts" => "MTA-STS",
        "tlsa" => "DANE",
        "no-policy-found" => "No policy",
        _ => policyType,
    };

    /// <summary>The date the report covers, as one date when it covers a single day. Senders write a day either as
    /// 00:00:00 to 23:59:59 or 00:00 to the next midnight; both are one day.</summary>
    public static string DateRange(DateTimeOffset beginUtc, DateTimeOffset endUtc)
    {
        var lastMoment = endUtc > beginUtc ? endUtc.AddSeconds(-1) : endUtc;
        var begin = UkTime.Date(beginUtc);
        var end = UkTime.Date(lastMoment);
        return begin == end ? begin : $"{begin} – {end}";
    }

    private static TlsrptPolicyDetail ReadPolicy(JsonElement element)
    {
        var policy = element.GetProperty("policy");
        var summary = element.GetProperty("summary");
        var lines = policy.TryGetProperty("policy-string", out var policyString) && policyString.ValueKind == JsonValueKind.Array
            ? policyString.EnumerateArray().Select(line => line.GetString() ?? "").ToList()
            : [];
        string? mode = null;
        string? maxAge = null;
        var mxHosts = new List<string>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            var key = line[..colon].Trim().ToLowerInvariant();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "mode": mode = value; break;
                case "mx": mxHosts.Add(value); break;
                case "max_age": maxAge = value; break;
            }
        }

        var failures = element.TryGetProperty("failure-details", out var details) && details.ValueKind == JsonValueKind.Array
            ? details.EnumerateArray().Select(failure => new TlsrptFailureLine(
                Text(failure, "result-type") ?? "unknown",
                failure.TryGetProperty("failed-session-count", out var count) && count.TryGetInt64(out var sessions) ? sessions : 0,
                Text(failure, "receiving-mx-hostname"),
                Text(failure, "receiving-ip"),
                Text(failure, "sending-mta-ip"),
                Text(failure, "failure-reason-code"),
                Text(failure, "additional-information"))).ToList()
            : [];
        return new TlsrptPolicyDetail(
            Text(policy, "policy-type") ?? "unknown",
            Text(policy, "policy-domain") ?? "",
            mode,
            mxHosts,
            maxAge,
            summary.GetProperty("total-successful-session-count").GetInt64(),
            summary.GetProperty("total-failure-session-count").GetInt64(),
            failures);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}

/// <summary>One policy in a TLS report. Mode, MxHosts and MaxAge come from the policy text the sender quoted, and are
/// empty when it didn't quote one.</summary>
public sealed record TlsrptPolicyDetail(
    string PolicyType,
    string PolicyDomain,
    string? Mode,
    IReadOnlyList<string> MxHosts,
    string? MaxAgeSeconds,
    long SuccessfulSessions,
    long FailedSessions,
    IReadOnlyList<TlsrptFailureLine> Failures)
{
    /// <summary>"mode enforce · max_age 604800" for the line under the policy; null when the sender quoted no policy.</summary>
    public string? Settings =>
        Mode is null && MaxAgeSeconds is null
            ? null
            : string.Join(" · ", new[] { Mode is null ? null : $"mode {Mode}", MaxAgeSeconds is null ? null : $"max_age {MaxAgeSeconds}" }.Where(part => part is not null));
}

/// <summary>One kind of failure in a policy: what failed, how many times, where, and the sender's own explanation.</summary>
public sealed record TlsrptFailureLine(
    string ResultType,
    long FailedSessions,
    string? ReceivingMxHostname,
    string? ReceivingIp,
    string? SendingMtaIp,
    string? FailureReasonCode,
    string? AdditionalInformation)
{
    /// <summary>"at mx.example.com (203.0.113.5), from 198.51.100.7": where it happened, or null when the sender didn't say.</summary>
    public string? Where
    {
        get
        {
            var receiving = (ReceivingMxHostname, ReceivingIp) switch
            {
                ({ } host, { } ip) => $"at {host} ({ip})",
                ({ } host, null) => $"at {host}",
                (null, { } ip) => $"at {ip}",
                _ => null,
            };
            var sending = SendingMtaIp is null ? null : $"from {SendingMtaIp}";
            var parts = new[] { receiving, sending }.Where(part => part is not null).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    public string Count => FailedSessions.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>What each TLS-RPT result type (RFC 8460 §4.3) means, in plain words, for the line under a failure.</summary>
public static class TlsrptResultTypes
{
    public static string? Explain(string resultType) => resultType.Trim().ToLowerInvariant() switch
    {
        "starttls-not-supported" => "The mail server didn't offer encryption (STARTTLS).",
        "certificate-host-mismatch" => "The mail server's certificate isn't for the name it was reached by.",
        "certificate-expired" => "The mail server's certificate has expired.",
        "certificate-not-trusted" => "The mail server's certificate isn't from a trusted authority, or its chain is incomplete.",
        "validation-failure" => "The connection didn't pass the policy for a reason not covered by the other types. Senders often use it when the mail server isn't one the policy they hold allows, for example while they still have an older copy of the policy.",
        "sts-policy-fetch-error" => "The sender couldn't download the MTA-STS policy from mta-sts.<domain>.",
        "sts-policy-invalid" => "The MTA-STS policy the sender downloaded wasn't valid.",
        "sts-webpki-invalid" => "The certificate on the mta-sts.<domain> website wasn't valid, so the policy couldn't be trusted.",
        "tlsa-invalid" => "The domain's DANE (TLSA) record didn't match the mail server's certificate.",
        "dnssec-invalid" => "DNSSEC validation failed for the domain's records.",
        "dane-required" => "The sender requires DANE, and the domain doesn't publish it.",
        _ => null,
    };
}
