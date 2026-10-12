using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Audit;

/// <summary>Applies an <see cref="AuditFilter"/> in the database, for the page's paging and for export.</summary>
public static class AuditQuery
{
    public static IQueryable<AuditEntry> Apply(IQueryable<AuditEntry> entries, AuditFilter filter)
    {
        if (filter.From is { } from)
        {
            var start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc >= start);
        }

        if (filter.To is { } to)
        {
            // The whole To day: everything before midnight at its end.
            var endExclusive = new DateTimeOffset(to.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            entries = entries.Where(entry => entry.OccurredUtc < endExclusive);
        }

        if (filter.Kind is { } kind)
        {
            entries = entries.Where(entry => entry.Kind == kind);
        }
        else if (!filter.IncludePageViews)
        {
            entries = entries.Where(entry => entry.Kind != AuditEntryKind.PageView);
        }

        if (LikePattern.Contains(filter.Who) is { } whoPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.ActorName, whoPattern, LikePattern.Escape)
                || (entry.ActorEmail != null && EF.Functions.ILike(entry.ActorEmail, whoPattern, LikePattern.Escape)));
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            entries = entries.Where(entry => entry.Action == filter.Action);
        }

        if (LikePattern.Contains(filter.Target) is { } targetPattern)
        {
            entries = entries.Where(entry => entry.TargetName != null && EF.Functions.ILike(entry.TargetName, targetPattern, LikePattern.Escape));
        }

        if (LikePattern.Contains(filter.Summary) is { } summaryPattern)
        {
            entries = entries.Where(entry => EF.Functions.ILike(entry.Summary, summaryPattern, LikePattern.Escape));
        }

        return entries;
    }
}
