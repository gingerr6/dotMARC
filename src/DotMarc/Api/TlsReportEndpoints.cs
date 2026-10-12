using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class TlsReportEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class TlsReportFilters
    {
        [FromQuery(Name = "reportingOrg")] public string[]? ReportingOrg { get; set; }
        [FromQuery(Name = "receivedAfter")] public string? ReceivedAfter { get; set; }
        [FromQuery(Name = "receivedBefore")] public string? ReceivedBefore { get; set; }
        [FromQuery(Name = "failuresOnly")] public string? FailuresOnly { get; set; }
        [FromQuery(Name = "resultType")] public string[]? ResultType { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains/{domain}/tls-reports", ListAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("ListTlsReports")
            .WithSummary("List a domain's TLS reports")
            .WithDescription($"The TLS-RPT reports received for the domain, newest first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("reportingOrg", "One or more reporters; each matches part of the name, in any letter case." + ApiFilterDocs.ListHint)
            .DocumentsFilter("receivedAfter", "Received at or after this date (2026-10-01) or time with a zone.")
            .DocumentsFilter("receivedBefore", "Received before this date or time.")
            .DocumentsFilter("failuresOnly", "true for reports with any failed sessions.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("resultType", "One or more failure result types, such as certificate-expired, in any letter case." + ApiFilterDocs.ListHint)
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

        api.MapGet("/domains/{domain}/tls-reports/{id:int}", GetAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetTlsReport")
            .WithSummary("Get one TLS report")
            .WithDescription($"One TLS-RPT report. A report of another domain is a 404. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/domains/{domain}/tls-reports/{id:int}/raw", GetRawAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetTlsReportJson")
            .WithSummary("Get a TLS report's original JSON")
            .WithDescription($"The report exactly as the sender sent it. {ApiDomainKey.Description}")
            .Produces<string>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static Task<int?> VisibleDomainIdAsync(DotMarcDbContext context, ClaimsPrincipal user, string domainKey, CancellationToken cancellationToken) =>
        ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey).Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<TlsrptReport> WithDetails(IQueryable<TlsrptReport> reports) =>
        reports.Include(report => report.Policies).ThenInclude(policy => policy.FailureDetails).AsSplitQuery();

    private static async Task<Results<Ok<ApiPage<ApiTlsReport>>, ValidationProblem, ProblemHttpResult>> ListAsync(
        [FromRoute(Name = "domain")] string domainKey, [AsParameters] TlsReportFilters filters, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var receivedAfter = reader.Date("receivedAfter", filters.ReceivedAfter);
        var receivedBefore = reader.Date("receivedBefore", filters.ReceivedBefore);
        reader.Range("receivedAfter", receivedAfter, "receivedBefore", receivedBefore);
        var failuresOnly = reader.Bool("failuresOnly", filters.FailuresOnly);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleDomainIdAsync(context, user, domainKey, cancellationToken) is not { } domainId)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var query = context.TlsrptReports.AsNoTracking().Where(report => report.DomainId == domainId);
        var orgPatterns = ApiFilterReader.Values(filters.ReportingOrg).Select(LikePattern.Contains).OfType<string>().ToList();
        if (orgPatterns.Count > 0)
        {
            query = query.Where(report => orgPatterns.Any(pattern => EF.Functions.ILike(report.ReportingOrg, pattern, LikePattern.Escape)));
        }

        if (receivedAfter is { } from) query = query.Where(report => report.ReceivedUtc >= from);
        if (receivedBefore is { } until) query = query.Where(report => report.ReceivedUtc < until);
        if (failuresOnly == true) query = query.Where(report => report.Policies.Any(policy => policy.FailedSessionCount > 0));
        var resultTypes = ApiFilterReader.Values(filters.ResultType).Select(type => type.ToLowerInvariant()).ToList();
        if (resultTypes.Count > 0)
        {
            query = query.Where(report => report.Policies.Any(policy => policy.FailureDetails.Any(failure => resultTypes.Contains(failure.ResultType.ToLower()))));
        }

        var ordered = query.OrderByDescending(report => report.ReceivedUtc).ThenByDescending(report => report.Id);
        var totalCount = await ordered.CountAsync(cancellationToken);
        var pageIds = await ordered.Skip((pageNumber - 1) * size).Take(size).Select(report => report.Id).ToListAsync(cancellationToken);
        var reports = (await WithDetails(context.TlsrptReports.AsNoTracking().Where(report => pageIds.Contains(report.Id))).ToListAsync(cancellationToken))
            .OrderBy(report => pageIds.IndexOf(report.Id))
            .Select(ApiTlsReport.From)
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiTlsReport>(reports, pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiTlsReport>, ProblemHttpResult>> GetAsync(
        [FromRoute(Name = "domain")] string domainKey, int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleDomainIdAsync(context, user, domainKey, cancellationToken) is not { } domainId)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var report = await WithDetails(context.TlsrptReports.AsNoTracking().Where(candidate => candidate.Id == id && candidate.DomainId == domainId))
            .SingleOrDefaultAsync(cancellationToken);
        return report is null ? ApiProblems.NotFound($"TLS report {id} for {domainKey}") : TypedResults.Ok(ApiTlsReport.From(report));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetRawAsync(
        [FromRoute(Name = "domain")] string domainKey, int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleDomainIdAsync(context, user, domainKey, cancellationToken) is not { } domainId)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var json = await context.TlsrptReports.AsNoTracking().Where(report => report.Id == id && report.DomainId == domainId)
            .Select(report => report.RawJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? ApiProblems.NotFound($"TLS report {id} for {domainKey}") : TypedResults.Text(json, "application/json");
    }
}
