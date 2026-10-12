using System.Security.Claims;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class ReportEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class ReportFilters
    {
        [FromQuery(Name = "reportingOrg")] public string[]? ReportingOrg { get; set; }
        [FromQuery(Name = "receivedAfter")] public string? ReceivedAfter { get; set; }
        [FromQuery(Name = "receivedBefore")] public string? ReceivedBefore { get; set; }
        [FromQuery(Name = "failingOnly")] public string? FailingOnly { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains/{domain}/reports", ListReportsAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("ListDmarcReports")
            .WithSummary("List a domain's DMARC reports")
            .WithDescription($"The aggregate reports received for the domain, newest first, with their records, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("reportingOrg", "One or more reporters; each matches part of the name, in any letter case." + ApiFilterDocs.ListHint)
            .DocumentsFilter("receivedAfter", "Received at or after this date (2026-10-01) or time with a zone.")
            .DocumentsFilter("receivedBefore", "Received before this date or time.")
            .DocumentsFilter("failingOnly", "true for reports with at least one failing record.", type: JsonSchemaType.Boolean)
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

        api.MapGet("/domains/{domain}/reports/{id:int}", GetReportAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetDmarcReport")
            .WithSummary("Get one DMARC report")
            .WithDescription($"One aggregate report with its records. A report of another domain is a 404. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/domains/{domain}/reports/{id:int}/raw", GetRawAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetDmarcReportXml")
            .WithSummary("Get a DMARC report's original XML")
            .WithDescription($"The report exactly as the receiver sent it. {ApiDomainKey.Description}")
            .Produces<string>(StatusCodes.Status200OK, "application/xml")
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static Task<int?> VisibleDomainIdAsync(DotMarcDbContext context, ClaimsPrincipal user, string domainKey, CancellationToken cancellationToken) =>
        ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey).Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);

    private static IQueryable<Report> WithDetails(IQueryable<Report> reports) =>
        reports.Include(report => report.Records).ThenInclude(record => record.OverrideReasons)
            .Include(report => report.Records).ThenInclude(record => record.AuthDetails)
            .AsSplitQuery();

    private static async Task<Results<Ok<ApiPage<ApiDmarcReport>>, ValidationProblem, ProblemHttpResult>> ListReportsAsync(
        [FromRoute(Name = "domain")] string domainKey, [AsParameters] ReportFilters filters, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var receivedAfter = reader.Date("receivedAfter", filters.ReceivedAfter);
        var receivedBefore = reader.Date("receivedBefore", filters.ReceivedBefore);
        reader.Range("receivedAfter", receivedAfter, "receivedBefore", receivedBefore);
        var failingOnly = reader.Bool("failingOnly", filters.FailingOnly);
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

        var query = context.Reports.AsNoTracking().Where(report => report.DomainId == domainId);
        var orgPatterns = ApiFilterReader.Values(filters.ReportingOrg).Select(LikePattern.Contains).OfType<string>().ToList();
        if (orgPatterns.Count > 0)
        {
            query = query.Where(report => orgPatterns.Any(pattern => EF.Functions.ILike(report.ReportingOrg, pattern, LikePattern.Escape)));
        }

        if (receivedAfter is { } from) query = query.Where(report => report.ReceivedUtc >= from);
        if (receivedBefore is { } until) query = query.Where(report => report.ReceivedUtc < until);
        if (failingOnly == true) query = query.Where(report => report.Records.Any(record => record.SpfResult != AuthResult.Pass && record.DkimResult != AuthResult.Pass));

        var ordered = query.OrderByDescending(report => report.ReceivedUtc).ThenByDescending(report => report.Id);
        var totalCount = await ordered.CountAsync(cancellationToken);
        var pageIds = await ordered.Skip((pageNumber - 1) * size).Take(size).Select(report => report.Id).ToListAsync(cancellationToken);
        var reports = (await WithDetails(context.Reports.AsNoTracking().Where(report => pageIds.Contains(report.Id))).ToListAsync(cancellationToken))
            .OrderBy(report => pageIds.IndexOf(report.Id))
            .Select(ApiDmarcReport.From)
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiDmarcReport>(reports, pageNumber, size, totalCount));
    }

    private static async Task<Results<Ok<ApiDmarcReport>, ProblemHttpResult>> GetReportAsync(
        [FromRoute(Name = "domain")] string domainKey, int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleDomainIdAsync(context, user, domainKey, cancellationToken) is not { } domainId)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var report = await WithDetails(context.Reports.AsNoTracking().Where(candidate => candidate.Id == id && candidate.DomainId == domainId))
            .SingleOrDefaultAsync(cancellationToken);
        return report is null ? ApiProblems.NotFound($"report {id} for {domainKey}") : TypedResults.Ok(ApiDmarcReport.From(report));
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetRawAsync(
        [FromRoute(Name = "domain")] string domainKey, int id, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await VisibleDomainIdAsync(context, user, domainKey, cancellationToken) is not { } domainId)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var xml = await context.Reports.AsNoTracking().Where(report => report.Id == id && report.DomainId == domainId)
            .Select(report => report.Raw!.Xml).SingleOrDefaultAsync(cancellationToken);
        return xml is null ? ApiProblems.NotFound($"report {id} for {domainKey}") : TypedResults.Text(xml, "application/xml");
    }
}
