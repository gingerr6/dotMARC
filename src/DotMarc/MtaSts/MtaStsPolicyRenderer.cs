using System.Security.Cryptography;
using System.Text;
using DotMarc.Data;

namespace DotMarc.MtaSts;

/// <summary>Renders a Domain's hosted MTA-STS policy as the plain-text body RFC 8461 §3.2
/// requires - a pure function, independent of DNS/HTTP/the database, so it needs no fake
/// anything to test.</summary>
public static class MtaStsPolicyRenderer
{
    public static string Render(MtaStsMode mode, IReadOnlyList<string> mxHosts, int maxAgeSeconds)
    {
        var lines = new List<string>
        {
            "version: STSv1",
            $"mode: {mode.ToString().ToLowerInvariant()}"
        };

        lines.AddRange(mxHosts.Select(host => $"mx: {host}"));
        lines.Add($"max_age: {maxAgeSeconds}");

        // RFC 8461 §3.2 requires the body to use CRLF line endings.
        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>The policy's id for the _mta-sts TXT record (RFC 8461 §3.1): taken from the policy itself, so it changes
    /// whenever the policy does, which is what tells senders to fetch it again. Hex, within the 32 letters and digits an
    /// id may have.</summary>
    public static string PolicyId(MtaStsMode mode, IReadOnlyList<string> mxHosts, int maxAgeSeconds) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Render(mode, mxHosts, maxAgeSeconds))))[..20].ToLowerInvariant();

    /// <summary>The value of the _mta-sts TXT record announcing a policy with this id.</summary>
    public static string PolicyRecord(string policyId) => $"v=STSv1; id={policyId}";
}
