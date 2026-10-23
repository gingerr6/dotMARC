using DotMarc.Data;
using DotMarc.Senders;

namespace DotMarc.Enforcement;

/// <summary>Where a domain is on the road from watching (p=none) to blocking spoofed mail (p=reject).</summary>
public enum EnforcementStage
{
    NoRecord,
    Monitoring,
    PartialQuarantine,
    Quarantine,
    PartialReject,
    Reject,
}

/// <summary>One sender's mail in the readiness window: how much, and how much passed DMARC.</summary>
public sealed record SenderVolume(SenderIdentity Identity, int Volume, int Passing);

/// <summary>Something standing between a domain and its next policy.</summary>
public sealed record ReadinessBlocker(string Title, string Detail);

public sealed record EnforcementAssessment(
    EnforcementStage Stage,
    DmarcPolicyLevel? NextPolicy,
    bool Ready,
    int DaysOfData,
    int Volume,
    double? ApprovedPassRate,
    IReadOnlyList<ReadinessBlocker> Blockers,
    bool StuckMonitoring)
{
    public bool Enforced => Stage == EnforcementStage.Reject;
}

/// <summary>Decides whether a domain can move to its next DMARC policy without its own mail being junked or bounced: two
/// weeks of reports, every sender with real volume reviewed (approved, or marked not ours), and approved senders passing
/// DMARC. Mail from senders marked not ours is expected to fail, and doesn't count against it.</summary>
public static class EnforcementReadiness
{
    /// <summary>How much reporting a domain needs before a step up, and the window it's judged over.</summary>
    public const int DaysNeeded = 14;

    /// <summary>The share of an approved sender's mail that must pass DMARC.</summary>
    public const double PassRateNeeded = 0.98;

    /// <summary>A sender below both of these is too small to hold a domain back: a stray forward or one-off.</summary>
    public const int MinVolumeToMatter = 50;
    public const double MinShareToMatter = 0.01;

    /// <summary>How long a domain can sit on p=none before it's flagged as stuck.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromDays(30);

    public static EnforcementStage StageOf(Domain domain)
    {
        if (domain.DmarcCheckStatus is DmarcCheckStatus.MissingOwnRecord || domain.DmarcPolicy is null)
        {
            return EnforcementStage.NoRecord;
        }

        var partial = domain.DmarcPercent is { } percent && percent < 100;
        return domain.DmarcPolicy switch
        {
            DmarcPolicyLevel.Reject => partial ? EnforcementStage.PartialReject : EnforcementStage.Reject,
            DmarcPolicyLevel.Quarantine => partial ? EnforcementStage.PartialQuarantine : EnforcementStage.Quarantine,
            _ => EnforcementStage.Monitoring,
        };
    }

    /// <summary>The policy to move to next: quarantine before reject, and the full 100% of the current one before the next.</summary>
    public static DmarcPolicyLevel? NextPolicyAfter(EnforcementStage stage) => stage switch
    {
        EnforcementStage.Monitoring or EnforcementStage.PartialQuarantine => DmarcPolicyLevel.Quarantine,
        EnforcementStage.Quarantine or EnforcementStage.PartialReject => DmarcPolicyLevel.Reject,
        _ => null,
    };

    public static EnforcementAssessment Assess(Domain domain, IReadOnlyList<SenderVolume> senders, IReadOnlyDictionary<string, SenderApprovalStatus> approvals,
        DateTimeOffset? firstReportUtc, DateTimeOffset nowUtc)
    {
        var stage = StageOf(domain);
        var next = NextPolicyAfter(stage);
        var volume = senders.Sum(sender => sender.Volume);
        var daysOfData = firstReportUtc is { } first ? Math.Max(0, (int)(nowUtc - first).TotalDays) : 0;
        var approved = senders.Where(sender => approvals.TryGetValue(sender.Identity.Key, out var status) && status == SenderApprovalStatus.Approved).ToList();
        var approvedVolume = approved.Sum(sender => sender.Volume);
        double? approvedPassRate = approvedVolume == 0 ? null : (double)approved.Sum(sender => sender.Passing) / approvedVolume;
        var stuck = stage == EnforcementStage.Monitoring && nowUtc - domain.FirstSeenUtc > StuckAfter;

        var blockers = new List<ReadinessBlocker>();
        if (stage == EnforcementStage.NoRecord)
        {
            blockers.Add(new("No DMARC record", "Add one first: it's on the setup checklist. Start it at p=none, to watch before enforcing."));
        }
        else if (next is not null)
        {
            if (daysOfData < DaysNeeded || volume == 0)
            {
                blockers.Add(new("Not enough reports yet",
                    volume == 0 ? "No mail has been reported for this domain yet." : $"{daysOfData} of the {DaysNeeded} days of reports needed to be sure who sends its mail."));
            }

            bool Matters(SenderVolume sender) => sender.Volume >= MinVolumeToMatter || (volume > 0 && (double)sender.Volume / volume >= MinShareToMatter);

            var unreviewed = senders.Where(sender => !approvals.ContainsKey(sender.Identity.Key) && !sender.Identity.IsForwarder && Matters(sender)).OrderByDescending(sender => sender.Volume).ToList();
            if (unreviewed.Count > 0)
            {
                blockers.Add(new($"{unreviewed.Count} sender{(unreviewed.Count == 1 ? "" : "s")} not reviewed",
                    $"Approve the ones that are the domain's own mail, and mark the rest not ours, on the Sources tab: {Describe(unreviewed)}."));
            }

            var failing = approved.Where(sender => Matters(sender) && (double)sender.Passing / sender.Volume < PassRateNeeded).OrderByDescending(sender => sender.Volume - sender.Passing).ToList();
            if (failing.Count > 0)
            {
                blockers.Add(new($"{failing.Count} approved sender{(failing.Count == 1 ? "" : "s")} failing DMARC",
                    $"Their mail would be {(next == DmarcPolicyLevel.Reject ? "rejected" : "junked")}. Fix their SPF or DKIM first: " +
                    string.Join(", ", failing.Take(5).Select(sender => $"{sender.Identity.Label} ({sender.Volume - sender.Passing} of {sender.Volume} failing)")) + "."));
            }
        }

        return new EnforcementAssessment(stage, next, next is not null && blockers.Count == 0, daysOfData, volume, approvedPassRate, blockers, stuck);

        static string Describe(IReadOnlyList<SenderVolume> list) =>
            string.Join(", ", list.Take(5).Select(sender => $"{sender.Identity.Label} ({sender.Volume:N0})")) + (list.Count > 5 ? $" and {list.Count - 5} more" : "");
    }

    public static string Describe(EnforcementStage stage) => stage switch
    {
        EnforcementStage.NoRecord => "No DMARC record",
        EnforcementStage.Monitoring => "Monitoring (p=none)",
        EnforcementStage.PartialQuarantine => "Quarantine, part of mail",
        EnforcementStage.Quarantine => "Quarantine",
        EnforcementStage.PartialReject => "Reject, part of mail",
        _ => "Reject: fully enforced",
    };
}
