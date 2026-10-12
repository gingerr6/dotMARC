using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using DotMarc.Audit;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class AuditEndpoints
{
    private const int DefaultPageSize = 50;

    private static readonly IReadOnlyList<string> Kinds = ["change", "signIn", "pageView", "all"];
    private static readonly IReadOnlyList<string> ActorKinds = ["user", "system", "apiKey"];
    private static readonly IReadOnlyList<string> TargetTypes = ["Domain", "Group", "Tag", "Alert", "Role", "UserAccess", "ApiKey", "Settings"];

    /// <summary>The audit log's filters; see the spec's section 2.</summary>
    public sealed class AuditFilters
    {
        [FromQuery(Name = "action")] public string[]? Action { get; set; }
        [FromQuery(Name = "kind")] public string[]? Kind { get; set; }
        [FromQuery(Name = "actorKind")] public string[]? ActorKind { get; set; }
        [FromQuery(Name = "actor")] public string? Actor { get; set; }
        [FromQuery(Name = "targetType")] public string[]? TargetType { get; set; }
        [FromQuery(Name = "targetId")] public string[]? TargetId { get; set; }
        [FromQuery(Name = "domain")] public string[]? Domain { get; set; }
        [FromQuery(Name = "occurredAfter")] public string? OccurredAfter { get; set; }
        [FromQuery(Name = "occurredBefore")] public string? OccurredBefore { get; set; }
        [FromQuery(Name = "search")] public string? Search { get; set; }
        [FromQuery(Name = "page")] public int? Page { get; set; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/audit", ListAuditAsync)
            .RequirePermission(Permission.AuditView)
            .WithTags(ApiTags.AuditLog)
            .WithName("ListAuditEntries")
            .WithSummary("List audit log entries")
            .WithDescription($"Who did what, newest first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Changes and sign-ins by default; ask for page views with kind. Filters combine: an entry must match every filter given. A key limited to certain groups sees only entries about its domains, its groups and their alerts.")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("action", "One or more action codes." + ApiFilterDocs.ListHint, AuditActions.All.Select(action => action.Action))
            .DocumentsFilter("kind", "change and signIn by default; pageView, or all for every kind." + ApiFilterDocs.ListHint, Kinds)
            .DocumentsFilter("actorKind", "Who acted: a person, dotMARC itself or an API key." + ApiFilterDocs.ListHint, ActorKinds)
            .DocumentsFilter("actor", "Part of the actor's name or email, in any letter case.")
            .DocumentsFilter("targetType", "One or more kinds of target." + ApiFilterDocs.ListHint, TargetTypes)
            .DocumentsFilter("targetId", "One or more target ids; use with targetType." + ApiFilterDocs.ListHint)
            .DocumentsFilter("domain", "One or more domains, by id or name: entries about the domain and its alerts. A domain this key can't see is a 404." + ApiFilterDocs.ListHint)
            .DocumentsFilter("occurredAfter", "At or after this date (2026-10-01) or time with a zone (2026-10-01T09:30:00Z).")
            .DocumentsFilter("occurredBefore", "Before this date or time.")
            .DocumentsFilter("search", "Text in the summary or the target's name, in any letter case.");
    }

    private static async Task<Results<Ok<ApiPage<ApiAuditEntry>>, ValidationProblem, ProblemHttpResult>> ListAuditAsync(
        [AsParameters] AuditFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var actions = reader.OneOf("action", filters.Action, AuditActions.All.Select(action => action.Action).ToList());
        var kinds = reader.OneOf("kind", filters.Kind, Kinds);
        var actorKinds = reader.OneOf("actorKind", filters.ActorKind, ActorKinds);
        var targetTypes = reader.OneOf("targetType", filters.TargetType, TargetTypes);
        var targetIds = ApiFilterReader.Values(filters.TargetId);
        var occurredAfter = reader.Date("occurredAfter", filters.OccurredAfter);
        var occurredBefore = reader.Date("occurredBefore", filters.OccurredBefore);
        reader.Range("occurredAfter", occurredAfter, "occurredBefore", occurredBefore);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AuditEntries.AsNoTracking();

        if (scope.IsScoped)
        {
            // A limited key sees entries about what it can see: its domains (by id, or by name where only the name was
            // recorded), its groups, and alerts on its domains. Sign-ins, settings, access and the rest stay hidden.
            var visible = await scope.Domains(context.Domains.AsNoTracking()).Select(domain => new { domain.Id, domain.Name }).ToListAsync(cancellationToken);
            var domainIds = visible.Select(domain => domain.Id.ToString(CultureInfo.InvariantCulture)).ToList();
            var domainNames = visible.Select(domain => domain.Name).ToList();
            var groupIds = (await scope.Groups(context.Groups.AsNoTracking()).Select(group => group.Id).ToListAsync(cancellationToken))
                .Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            query = query.Where(entry =>
                (entry.TargetType == "Domain" && ((entry.TargetId != null && domainIds.Contains(entry.TargetId))
                    || (entry.TargetId == null && entry.TargetName != null && domainNames.Contains(entry.TargetName))))
                || (entry.TargetType == "Group" && entry.TargetId != null && groupIds.Contains(entry.TargetId))
                || (entry.TargetType == "Alert" && entry.TargetName != null && domainNames.Contains(entry.TargetName)));
        }

        if (!kinds.Contains("all"))
        {
            if (kinds.Count > 0)
            {
                var chosenKinds = kinds.Select(kind => Enum.Parse<AuditEntryKind>(kind, ignoreCase: true)).ToList();
                query = query.Where(entry => chosenKinds.Contains(entry.Kind));
            }
            else
            {
                query = query.Where(entry => entry.Kind != AuditEntryKind.PageView);
            }
        }

        if (actions.Count > 0)
        {
            query = query.Where(entry => actions.Contains(entry.Action));
        }

        if (actorKinds.Count > 0)
        {
            var chosenActors = actorKinds.Select(kind => Enum.Parse<AuditActorKind>(kind, ignoreCase: true)).ToList();
            query = query.Where(entry => chosenActors.Contains(entry.ActorKind));
        }

        if (LikePattern.Contains(filters.Actor) is { } actorPattern)
        {
            query = query.Where(entry => EF.Functions.ILike(entry.ActorName, actorPattern, LikePattern.Escape)
                || (entry.ActorEmail != null && EF.Functions.ILike(entry.ActorEmail, actorPattern, LikePattern.Escape)));
        }

        if (targetTypes.Count > 0)
        {
            query = query.Where(entry => entry.TargetType != null && targetTypes.Contains(entry.TargetType));
        }

        if (targetIds.Count > 0)
        {
            query = query.Where(entry => entry.TargetId != null && targetIds.Contains(entry.TargetId));
        }

        var domainKeys = ApiFilterReader.Values(filters.Domain);
        if (domainKeys.Count > 0)
        {
            var ids = new List<string>();
            var names = new List<string>();
            foreach (var domainKey in domainKeys)
            {
                var domain = await ApiDomainKey.Matching(scope.Domains(context.Domains.AsNoTracking()), domainKey)
                    .Select(candidate => new { candidate.Id, candidate.Name }).SingleOrDefaultAsync(cancellationToken);
                if (domain is null)
                {
                    return ApiProblems.NotFound($"domain {domainKey}");
                }

                ids.Add(domain.Id.ToString(CultureInfo.InvariantCulture));
                names.Add(domain.Name);
            }

            query = query.Where(entry =>
                (entry.TargetType == "Domain" && ((entry.TargetId != null && ids.Contains(entry.TargetId))
                    || (entry.TargetId == null && entry.TargetName != null && names.Contains(entry.TargetName))))
                || (entry.TargetType == "Alert" && entry.TargetName != null && names.Contains(entry.TargetName)));
        }

        if (occurredAfter is { } from)
        {
            query = query.Where(entry => entry.OccurredUtc >= from);
        }

        if (occurredBefore is { } until)
        {
            query = query.Where(entry => entry.OccurredUtc < until);
        }

        if (LikePattern.Contains(filters.Search) is { } searchPattern)
        {
            query = query.Where(entry => EF.Functions.ILike(entry.Summary, searchPattern, LikePattern.Escape)
                || (entry.TargetName != null && EF.Functions.ILike(entry.TargetName, searchPattern, LikePattern.Escape)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var entries = await query
            .OrderByDescending(entry => entry.OccurredUtc)
            .ThenByDescending(entry => entry.Id)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .ToListAsync(cancellationToken);
        var items = entries.Select(entry => new ApiAuditEntry(
                entry.Id,
                entry.OccurredUtc,
                JsonNamingPolicy.CamelCase.ConvertName(entry.Kind.ToString()),
                new ApiAuditActor(JsonNamingPolicy.CamelCase.ConvertName(entry.ActorKind.ToString()), entry.ActorName, entry.ActorEmail),
                entry.Action,
                entry.TargetType is null ? null : new ApiAuditTarget(entry.TargetType, entry.TargetId, entry.TargetName),
                entry.Summary,
                entry.Changes.Select(change => HidesValues(scope, entry, change)
                    ? new ApiAuditChange(change.Field, null, null, change.Secret)
                    : new ApiAuditChange(change.Field, change.Old, change.New, change.Secret)).ToList()))
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiAuditEntry>(items, pageNumber, size, totalCount));
    }

    /// <summary>A domain's group change lists every group by name, including other clients' groups, which a key limited
    /// to certain groups must never learn. Such a key sees that the groups changed, without the names.</summary>
    private static bool HidesValues(ApiScope scope, AuditEntry entry, AuditFieldChange change) =>
        scope.IsScoped && entry.Action == AuditActions.DomainGroupsChanged && change.Field == "Groups";
}
