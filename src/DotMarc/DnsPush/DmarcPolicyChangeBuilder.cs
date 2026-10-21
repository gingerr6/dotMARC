using DotMarc.Data;

namespace DotMarc.DnsPush;

/// <summary>Moves a domain's DMARC record to a stricter policy (the payload: quarantine or reject), for enforcement
/// readiness's next step. Everything else in the record is kept, except pct, which goes so the new policy covers all
/// mail. A record delegated by CNAME belongs to someone else, so it's left alone.</summary>
public sealed class DmarcPolicyChangeBuilder(IDmarcTxtLookup dmarcTxtLookup) : IDnsChangeBuilder
{
    public string Target => "dmarc-policy";
    public string RequiredPolicy => "DomainsEdit";
    public bool WritesToDomainZone => true;

    public async Task<DnsChangePlan> BuildAsync(DnsPushRequest request, CancellationToken cancellationToken)
    {
        if (ParsePolicy(request.Payload) is not { } policy)
        {
            return DnsChangePlan.Refuse("error");
        }

        var domainName = request.Domain.Name;
        var existing = await dmarcTxtLookup.LookupAsync(domainName, cancellationToken).ConfigureAwait(false);
        if (existing.DelegatedToCname is not null || existing.DirectValue is null)
        {
            return DnsChangePlan.Refuse("error");
        }

        var updated = WithPolicy(existing.DirectValue, policy);
        return updated == existing.DirectValue
            ? DnsChangePlan.Of()
            : DnsChangePlan.Of(new DnsRecordChange(DnsRecordChangeKind.Merge, "TXT", $"_dmarc.{domainName}", updated, existing.DirectValue, request.DomainZone!));
    }

    public static DmarcPolicyLevel? ParsePolicy(string? payload) => payload?.Trim().ToLowerInvariant() switch
    {
        "quarantine" => DmarcPolicyLevel.Quarantine,
        "reject" => DmarcPolicyLevel.Reject,
        _ => null,
    };

    /// <summary>The record with p= set to the policy and pct= dropped; every other tag kept, in order.</summary>
    public static string WithPolicy(string record, DmarcPolicyLevel policy)
    {
        var value = policy.ToString().ToLowerInvariant();
        var tags = record.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(tag => !tag.StartsWith("pct=", StringComparison.OrdinalIgnoreCase))
            .Select(tag => tag.StartsWith("p=", StringComparison.OrdinalIgnoreCase) ? $"p={value}" : tag)
            .ToList();
        if (!tags.Any(tag => tag.StartsWith("p=", StringComparison.OrdinalIgnoreCase)))
        {
            tags.Insert(Math.Min(1, tags.Count), $"p={value}");
        }

        return string.Join("; ", tags);
    }
}
