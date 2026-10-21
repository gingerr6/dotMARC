using DotMarc.Data;

namespace DotMarc.Dns;

/// <summary>A subdomain that receivers checked SPF on for a domain's mail: a mail service's bounce (Return-Path)
/// domain, usually a CNAME to the service, such as em1234.contoso.com for SMTP2GO.</summary>
public sealed record ReturnPathDomain(string Name, int Volume, int Passing, IReadOnlyList<string> SourceIps)
{
    public double PassRate => Volume == 0 ? 0 : (double)Passing / Volume;
}

public static class ReturnPathDomains
{
    /// <summary>The subdomains of the domain that reports name as the SPF (MAIL FROM) domain, busiest first. SPF passing
    /// on one of them aligns with the domain under DMARC's default relaxed alignment, whatever the domain's own SPF
    /// record says. HELO checks aren't counted: they say nothing about the bounce address.</summary>
    public static IReadOnlyList<ReturnPathDomain> From(string domainName, IEnumerable<Report> reports)
    {
        var suffix = "." + domainName.TrimEnd('.');
        return reports
            .SelectMany(report => report.Records)
            .SelectMany(record => record.AuthDetails
                .Where(detail => detail.Mechanism == DmarcAuthMechanism.Spf && !string.Equals(detail.Scope, "Helo", StringComparison.OrdinalIgnoreCase))
                .Select(detail => (Name: detail.Domain.Trim().TrimEnd('.').ToLowerInvariant(), detail.Result, record)))
            .Where(item => item.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.Name)
            .Select(group => new ReturnPathDomain(
                group.Key,
                group.Sum(item => item.record.MessageCount),
                group.Where(item => item.Result == DmarcMechanismResult.Pass).Sum(item => item.record.MessageCount),
                group.Select(item => item.record.SourceIp).Distinct().ToList()))
            .OrderByDescending(domain => domain.Volume)
            .ToList();
    }
}
