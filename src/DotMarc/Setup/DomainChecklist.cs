using DotMarc.Data;

namespace DotMarc.Setup;

public enum ChecklistState
{
    /// <summary>In place and checked.</summary>
    Done,

    /// <summary>Missing or wrong: something to change in DNS.</summary>
    ToDo,

    /// <summary>Not checked yet, so neither ticked nor crossed.</summary>
    Waiting,

    /// <summary>Not set up, and not needed until it is (MTA-STS while it's off, DKIM before its selectors are known).</summary>
    NotStarted,
}

/// <summary>One record a domain needs. ChangeTarget names the IDnsChangeBuilder that works out the record to add, when
/// one can; ByProvider marks a record the MSP adds rather than the client (it's in the MSP's own zone, or needs setting
/// up in dotMARC first).</summary>
public sealed record ChecklistItem(string Key, string Title, string Why, ChecklistState State, string? Detail, string? ChangeTarget, bool ByProvider);

/// <summary>The DNS records a domain needs for dotMARC to protect it, each ticked from the checks dotMARC already runs on
/// it, for the setup checklist on the domain page, the client's Setup page and the client portal.</summary>
public static class DomainChecklist
{
    public static IReadOnlyList<ChecklistItem> For(Domain domain)
    {
        var items = new List<ChecklistItem>
        {
            new("dmarc", "DMARC record", "Tells receivers what to do with mail that fails checks, and to send reports to dotMARC.",
                domain.DmarcCheckStatus switch
                {
                    DmarcCheckStatus.Ok or DmarcCheckStatus.MissingAuthorizationRecord => ChecklistState.Done,
                    DmarcCheckStatus.NotChecked => ChecklistState.Waiting,
                    _ => ChecklistState.ToDo,
                },
                domain.DmarcCheckDetail, "dmarc", ByProvider: false),
            new("spf", "SPF record", "Lists the servers allowed to send as the domain.",
                domain.SpfCheckStatus switch
                {
                    SpfCheckStatus.Ok or SpfCheckStatus.NullSpf => ChecklistState.Done,
                    SpfCheckStatus.NotChecked => ChecklistState.Waiting,
                    _ => ChecklistState.ToDo,
                },
                domain.SpfCheckDetail, ChangeTarget: null, ByProvider: false),
            new("dkim", "DKIM records", "Let receivers check the domain's mail really came from it, unchanged.",
                domain.DkimCheckStatus switch
                {
                    DkimCheckStatus.Ok => ChecklistState.Done,
                    DkimCheckStatus.NotConfigured => ChecklistState.NotStarted,
                    _ => ChecklistState.ToDo,
                },
                domain.DkimCheckStatus == DkimCheckStatus.NotConfigured ? "The domain's DKIM selectors haven't been set up in dotMARC yet." : domain.DkimCheckDetail,
                domain.DkimRecords.Count > 0 ? "dkim" : null, ByProvider: false),
            new("tlsrpt", "TLS reporting record", "Asks other mail servers to report problems delivering to the domain securely.",
                domain.TlsrptCheckStatus switch
                {
                    TlsrptCheckStatus.Ok => ChecklistState.Done,
                    TlsrptCheckStatus.NotChecked => ChecklistState.Waiting,
                    _ => ChecklistState.ToDo,
                },
                domain.TlsrptCheckDetail, "tlsrpt", ByProvider: false),
            new("mta-sts", "MTA-STS records", "Make other mail servers deliver to the domain only over a secure connection.",
                !domain.MtaStsEnabled ? ChecklistState.NotStarted
                    : domain.MtaStsStatus == MtaStsStatus.Active ? ChecklistState.Done
                    : domain.MtaStsStatus == MtaStsStatus.NotConfigured ? ChecklistState.Waiting
                    : ChecklistState.ToDo,
                domain.MtaStsEnabled ? domain.MtaStsCheckDetail : "MTA-STS hasn't been turned on for this domain yet.",
                domain.MtaStsEnabled ? "mta-sts" : null, ByProvider: false),
        };

        // The record that lets another domain's mailbox take this domain's reports lives in the mailbox's own zone, so the
        // MSP adds it, and only when the reports go to a mailbox on another domain.
        if (domain.DmarcAuthorizationCheckStatus is not DmarcAuthorizationCheckStatus.NotApplicable)
        {
            items.Insert(1, new("dmarc-auth", "Permission to receive reports", "Lets dotMARC's mailbox accept the domain's DMARC reports.",
                domain.DmarcAuthorizationCheckStatus switch
                {
                    DmarcAuthorizationCheckStatus.Ok => ChecklistState.Done,
                    DmarcAuthorizationCheckStatus.NotChecked => ChecklistState.Waiting,
                    _ => ChecklistState.ToDo,
                },
                domain.DmarcAuthorizationCheckDetail, "dmarc-auth", ByProvider: true));
        }

        return items;
    }

    /// <summary>Done out of what's needed: MTA-STS and DKIM count once they're set up in dotMARC.</summary>
    public static (int Done, int Total) Progress(IReadOnlyList<ChecklistItem> items)
    {
        var counted = items.Where(item => item.State != ChecklistState.NotStarted).ToList();
        return (counted.Count(item => item.State == ChecklistState.Done), counted.Count);
    }

    public static (int Done, int Total) Progress(Domain domain) => Progress(For(domain));
}
