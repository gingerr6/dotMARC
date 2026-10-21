using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;
using DotMarc.Data;

namespace DotMarc.Dns;

public sealed record BimiCheckResult(BimiCheckStatus Status, string? Detail, string? LogoUrl, DateTimeOffset? CertificateExpiresUtc);

public interface IBimiChecker
{
    Task<BimiCheckResult> CheckAsync(Domain domain, CancellationToken cancellationToken);
}

/// <summary>Checks a domain's readiness for BIMI (a logo shown beside its mail): its record at default._bimi.&lt;domain&gt;,
/// that DMARC is enforced (quarantine or reject, for all mail), that the logo is an SVG Tiny PS file as mailboxes require,
/// and that the mark certificate (VMC or CMC), when there is one, is current.</summary>
public sealed class BimiChecker(HttpClient dnsHttp, PublicHttpFetcher fetcher, TimeProvider time) : IBimiChecker
{
    public const int MaxLogoBytes = 32 * 1024;
    public const int MaxCertificateBytes = 64 * 1024;

    /// <summary>The extended key usage a mark certificate carries: id-kp-BrandIndicatorforMessageIdentification.</summary>
    public const string BimiKeyUsageOid = "1.3.6.1.5.5.7.3.31";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BimiCheckResult> CheckAsync(Domain domain, CancellationToken cancellationToken)
    {
        var recordName = $"default._bimi.{domain.Name}";
        var record = await QueryTxtAsync(recordName, cancellationToken).ConfigureAwait(false);
        var enforced = IsEnforced(domain);
        if (record is null)
        {
            return new(BimiCheckStatus.NoRecord, enforced
                ? "Ready for BIMI: DMARC is enforced. Publish a BIMI record with the logo (and a mark certificate for Gmail and Apple Mail)."
                : "Not ready for BIMI yet: DMARC must be at p=quarantine or p=reject for all mail first.", null, null);
        }

        var tags = ParseTags(record);
        if (!string.Equals(tags.GetValueOrDefault("v"), "BIMI1", StringComparison.OrdinalIgnoreCase))
        {
            return new(BimiCheckStatus.Misconfigured, $"{recordName} doesn't start with v=BIMI1: {record}", null, null);
        }

        var logoUrl = tags.GetValueOrDefault("l");
        if (string.IsNullOrWhiteSpace(logoUrl))
        {
            return new(BimiCheckStatus.Misconfigured, $"{recordName} has no logo (l=), so it declines BIMI.", null, null);
        }

        var logo = await fetcher.GetAsync(logoUrl, MaxLogoBytes, cancellationToken).ConfigureAwait(false);
        if (logo.Error is { } logoError)
        {
            return new(BimiCheckStatus.Misconfigured, $"The logo at {logoUrl} can't be used: {logoError}.", null, null);
        }

        var logoProblems = LogoProblems(logo.Body!);
        if (logoProblems.Count > 0)
        {
            return new(BimiCheckStatus.Misconfigured, $"The logo isn't SVG Tiny PS, which mailboxes require: {string.Join("; ", logoProblems)}.", null, null);
        }

        DateTimeOffset? expires = null;
        var certificateUrl = tags.GetValueOrDefault("a");
        if (!string.IsNullOrWhiteSpace(certificateUrl))
        {
            var certificate = await fetcher.GetAsync(certificateUrl, MaxCertificateBytes, cancellationToken).ConfigureAwait(false);
            if (certificate.Error is { } certificateError)
            {
                return new(BimiCheckStatus.Misconfigured, $"The mark certificate at {certificateUrl} can't be used: {certificateError}.", logoUrl, null);
            }

            var (notAfter, problem) = InspectCertificate(certificate.Body!, time.GetUtcNow());
            if (problem is not null)
            {
                return new(BimiCheckStatus.Misconfigured, $"The mark certificate can't be used: {problem}.", logoUrl, notAfter);
            }
            expires = notAfter;
        }

        if (!enforced)
        {
            return new(BimiCheckStatus.NotEnforced, "The BIMI record and logo are fine, but no mailbox shows the logo until DMARC is at p=quarantine or p=reject for all mail.", logoUrl, expires);
        }

        return expires is null
            ? new(BimiCheckStatus.LogoOnly, "The logo shows in Yahoo, Fastmail and others. Gmail and Apple Mail also need a mark certificate (VMC or CMC) in the record's a= tag.", logoUrl, null)
            : new(BimiCheckStatus.Ok, $"The logo shows in Gmail, Apple Mail, Yahoo and others. The mark certificate expires on {Reporting.UkTime.Date(expires.Value)}.", logoUrl, expires);
    }

    /// <summary>DMARC at quarantine or reject for all mail (no pct= below 100), which every mailbox showing BIMI requires.</summary>
    public static bool IsEnforced(Domain domain) =>
        domain.DmarcCheckStatus is DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord
        && domain.DmarcPolicy is DmarcPolicyLevel.Quarantine or DmarcPolicyLevel.Reject
        && domain.DmarcPercent is null or 100;

    public static Dictionary<string, string> ParseTags(string record) => record
        .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
        .Where(pair => pair.Length == 2)
        .GroupBy(pair => pair[0].ToLowerInvariant())
        .ToDictionary(group => group.Key, group => group.First()[1]);

    /// <summary>Why a logo isn't SVG Tiny PS (the BIMI profile of SVG), or nothing when it's fine.</summary>
    public static List<string> LogoProblems(byte[] svg)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(svg), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return ["it isn't a valid SVG file"];
        }

        var root = document.Root!;
        if (root.Name.LocalName != "svg")
        {
            return ["it isn't an SVG file"];
        }

        var problems = new List<string>();
        if ((string?)root.Attribute("baseProfile") != "tiny-ps")
        {
            problems.Add("its <svg> needs baseProfile=\"tiny-ps\"");
        }
        if ((string?)root.Attribute("version") != "1.2")
        {
            problems.Add("its <svg> needs version=\"1.2\"");
        }
        if (!root.Elements().Any(element => element.Name.LocalName == "title" && !string.IsNullOrWhiteSpace(element.Value)))
        {
            problems.Add("it needs a <title> with the company's name");
        }
        if (root.Descendants().Any(element => element.Name.LocalName is "script" or "image" or "foreignObject" or "animate" or "set"))
        {
            problems.Add("it mustn't contain scripts, embedded images or animation");
        }
        if (root.DescendantsAndSelf().Attributes().Any(attribute => attribute.Name.LocalName == "href" && !attribute.Value.StartsWith('#')))
        {
            problems.Add("it mustn't link to anything outside the file");
        }
        if (root.Attributes().Any(attribute => attribute.Name.LocalName is "x" or "y"))
        {
            problems.Add("its <svg> mustn't have x= or y=");
        }
        if (((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is { Length: 4 } box
            && double.TryParse(box[2], System.Globalization.CultureInfo.InvariantCulture, out var width)
            && double.TryParse(box[3], System.Globalization.CultureInfo.InvariantCulture, out var height)
            && Math.Abs(width - height) > 0.5)
        {
            problems.Add("it must be square");
        }

        return problems;
    }

    /// <summary>When a PEM mark certificate expires, and why it can't be used, if it can't.</summary>
    public static (DateTimeOffset? NotAfter, string? Problem) InspectCertificate(byte[] pem, DateTimeOffset nowUtc)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(System.Text.Encoding.ASCII.GetString(pem));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return (null, "it isn't a PEM certificate");
        }

        using (certificate)
        {
            var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
            if (notAfter < nowUtc)
            {
                return (notAfter, $"it expired on {Reporting.UkTime.Date(notAfter)}");
            }

            var isMark = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .Any(extension => extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(oid => oid.Value == BimiKeyUsageOid));
            return isMark ? (notAfter, null) : (notAfter, "it isn't a mark certificate (VMC or CMC)");
        }
    }

    private async Task<string?> QueryTxtAsync(string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
        request.Headers.Accept.ParseAdd("application/dns-json");
        var response = await dnsHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<DnsOverHttpsResponse>(body, JsonOptions)!;
        return parsed.Answer?
            .Where(answer => answer.Type == 16)
            .Select(answer => string.Join("", answer.Data.Split("\" \"")).Trim('"'))
            .FirstOrDefault(value => value.StartsWith("v=BIMI", StringComparison.OrdinalIgnoreCase))
            ?? parsed.Answer?.Where(answer => answer.Type == 16).Select(answer => answer.Data.Trim('"')).FirstOrDefault();
    }

    private sealed record DnsOverHttpsResponse([property: JsonPropertyName("Answer")] List<DnsAnswer>? Answer);
    private sealed record DnsAnswer([property: JsonPropertyName("type")] int Type, [property: JsonPropertyName("data")] string Data);
}
