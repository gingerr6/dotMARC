using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class ParseFailureEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class ParseFailureFilters
    {
        [FromQuery(Name = "search")] public string? Search { get; set; }
        [FromQuery(Name = "minAttempts")] public string? MinAttempts { get; set; }
        [FromQuery(Name = "lastAttemptAfter")] public string? LastAttemptAfter { get; set; }
        [FromQuery(Name = "lastAttemptBefore")] public string? LastAttemptBefore { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/parse-failures", ListAsync)
            .RequirePermission(Permission.DomainsEdit)
            .WithTags(ApiTags.ParseFailures)
            .WithName("ListParseFailures")
            .WithSummary("List parse failures")
            .WithDescription($"Report emails dotMARC couldn't read, most recently tried first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. dotMARC retries them on every poll. A key limited to certain groups is refused, since failures belong to no domain.")
            .DocumentsFilter("search", "Part of the reason, in any letter case.")
            .DocumentsFilter("minAttempts", "Tried at least this many times.", type: JsonSchemaType.Integer)
            .DocumentsFilter("lastAttemptAfter", "Last tried at or after this date (2026-10-01) or time with a zone.")
            .DocumentsFilter("lastAttemptBefore", "Last tried before this date or time.")
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

    private static async Task<Results<Ok<ApiPage<ApiParseFailure>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [AsParameters] ParseFailureFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        if (ApiScope.From(user).IsScoped)
        {
            return ApiProblems.Forbidden("A key limited to certain groups can't read parse failures, which belong to no domain. Use a key that isn't limited to groups.");
        }

        var reader = new ApiFilterReader();
        var minAttempts = reader.Integer("minAttempts", filters.MinAttempts, 1, int.MaxValue);
        var after = reader.Date("lastAttemptAfter", filters.LastAttemptAfter);
        var before = reader.Date("lastAttemptBefore", filters.LastAttemptBefore);
        reader.Range("lastAttemptAfter", after, "lastAttemptBefore", before);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.ParseFailures.AsNoTracking();
        if (LikePattern.Contains(filters.Search) is { } pattern) query = query.Where(failure => EF.Functions.ILike(failure.Reason, pattern, LikePattern.Escape));
        if (minAttempts is { } atLeast) query = query.Where(failure => failure.AttemptCount >= atLeast);
        if (after is { } from) query = query.Where(failure => failure.LastAttemptedUtc >= from);
        if (before is { } until) query = query.Where(failure => failure.LastAttemptedUtc < until);
        var totalCount = await query.CountAsync(cancellationToken);
        var failures = await query.OrderByDescending(failure => failure.LastAttemptedUtc).ThenByDescending(failure => failure.Id)
            .Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiPage<ApiParseFailure>(
            failures.Select(failure => new ApiParseFailure(failure.Id, failure.GraphMessageId, failure.Reason, failure.AttemptCount, failure.LastAttemptedUtc)).ToList(),
            pageNumber, size, totalCount));
    }
}
