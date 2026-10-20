using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Senders;

/// <summary>Reads and records decisions about a domain's senders (see SenderApproval), audited like other domain
/// edits.</summary>
public static class SenderApprovalService
{
    public static async Task<Dictionary<string, SenderApproval>> GetAsync(DotMarcDbContext context, int domainId, CancellationToken cancellationToken = default) =>
        await context.SenderApprovals
            .AsNoTracking()
            .Where(approval => approval.DomainId == domainId)
            .ToDictionaryAsync(approval => approval.SenderKey, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Records a decision about one sender, or clears it with a null status so the sender is unreviewed
    /// again.</summary>
    public static async Task SetAsync(DotMarcDbContext context, AuditActor actor, int domainId, string senderKey, SenderApprovalStatus? status, CancellationToken cancellationToken = default)
    {
        var domain = await context.Domains.SingleAsync(d => d.Id == domainId, cancellationToken).ConfigureAwait(false);
        var existing = await context.SenderApprovals
            .SingleOrDefaultAsync(approval => approval.DomainId == domainId && approval.SenderKey == senderKey, cancellationToken)
            .ConfigureAwait(false);
        if (existing?.Status == status)
        {
            return;
        }

        var changes = new AuditChanges().Field("Status", Describe(existing?.Status), Describe(status));
        if (status is { } newStatus)
        {
            if (existing is null)
            {
                existing = new SenderApproval { DomainId = domainId, SenderKey = senderKey };
                context.SenderApprovals.Add(existing);
            }

            existing.Status = newStatus;
            existing.DecidedBy = actor.Email ?? actor.Name;
            existing.DecidedUtc = DateTimeOffset.UtcNow;
        }
        else if (existing is not null)
        {
            context.SenderApprovals.Remove(existing);
        }

        var label = SenderIdentity.LabelFor(senderKey);
        AuditLog.Record(context, actor, AuditActions.DomainSenderReviewed, AuditTarget.For(domain),
            $"Marked {label} as {Describe(status).ToLowerInvariant()} for {domain.Name}", changes);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string Describe(SenderApprovalStatus? status) => status switch
    {
        SenderApprovalStatus.Approved => "Approved",
        SenderApprovalStatus.NotOurs => "Not ours",
        _ => "Unreviewed",
    };
}
