using System.Globalization;
using System.Security.Claims;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Reporting.ClientReports;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class ClientReportEndpoints
{
    private const int DefaultPageSize = 50;
    private static readonly IReadOnlyList<string> PeriodKinds = ["week", "month", "quarter"];

    public sealed class DeliveryFilters
    {
        [FromQuery(Name = "status")] public string[]? Status { get; set; }
        [FromQuery(Name = "kind")] public string[]? Kind { get; set; }
        [FromQuery(Name = "sentAfter")] public string? SentAfter { get; set; }
        [FromQuery(Name = "sentBefore")] public string? SentBefore { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    public static void Map(RouteGroupBuilder api)
    {
        const string GroupNote = " A group this key can't see is a 404.";

        api.MapGet("/groups/{group:int}/report-schedule", GetScheduleAsync)
            .RequirePermission(Permission.ReportsManage)
            .WithTags(ApiTags.ClientReports)
            .WithName("GetReportSchedule")
            .WithSummary("Get a group's report schedule")
            .WithDescription("How often the group's report is sent and to whom, and the next period and when it's due. With no schedule, frequency is Off." + GroupNote)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/groups/{group:int}/report-deliveries", ListDeliveriesAsync)
            .RequirePermission(Permission.ReportsManage)
            .WithTags(ApiTags.ClientReports)
            .WithName("ListReportDeliveries")
            .WithSummary("List a group's report deliveries")
            .WithDescription($"Scheduled and manual sends, newest first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine." + GroupNote)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("status", "One or more delivery statuses." + ApiFilterDocs.ListHint, Enum.GetNames<ClientReportDeliveryStatus>())
            .DocumentsFilter("kind", "Scheduled or Manual." + ApiFilterDocs.ListHint, Enum.GetNames<ClientReportDeliveryKind>())
            .DocumentsFilter("sentAfter", "Sent at or after this date (2026-10-01) or time with a zone. Unsent deliveries never match.")
            .DocumentsFilter("sentBefore", "Sent before this date or time. Unsent deliveries never match.")
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);

        api.MapGet("/groups/{group:int}/report", DownloadAsync)
            .RequirePermission(Permission.ReportsManage)
            .WithTags(ApiTags.ClientReports)
            .WithName("GetReportPdf")
            .WithSummary("Download a group's report as a PDF")
            .WithDescription("The report for one period, as the Reports dialog downloads it. Give period with of, or start and end." + GroupNote)
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("period", "week, month or quarter, with of.", PeriodKinds)
            .DocumentsFilter("of", "A date inside the period (default: today in the report time zone).")
            .DocumentsFilter("start", "A custom range's first day, such as 2026-09-01, with end.")
            .DocumentsFilter("end", "A custom range's last day, included.");

        api.MapPost("/groups/{group:int}/report/send", SendAsync)
            .RequirePermission(Permission.ReportsManage)
            .WithTags(ApiTags.ClientReports)
            .WithName("SendReport")
            .WithSummary("Send a group's report now")
            .WithDescription("Sends the report for a period, as Send now in the Reports dialog: recorded as a manual delivery and audited. recipients defaults to the group's schedule. Returns the delivery, sent or failed." + GroupNote)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<bool> MayReachAsync(DotMarcDbContext context, ClaimsPrincipal user, int groupId, CancellationToken cancellationToken) =>
        ApiScope.From(user).Includes(groupId) && await context.Groups.AnyAsync(group => group.Id == groupId, cancellationToken);

    private static async Task<Results<Ok<ApiReportSchedule>, ProblemHttpResult>> GetScheduleAsync(
        [FromRoute(Name = "group")] int groupId, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayReachAsync(context, user, groupId, cancellationToken))
        {
            return ApiProblems.NotFound($"group {groupId}");
        }

        var schedule = await ClientReportService.GetScheduleAsync(context, groupId, cancellationToken);
        if (schedule is null || schedule.Frequency == ReportFrequency.Off)
        {
            return TypedResults.Ok(new ApiReportSchedule(nameof(ReportFrequency.Off), schedule?.Recipients ?? [], schedule?.StartedUtc, null, null));
        }

        var settings = await ReportSettingsService.GetAsync(context, cancellationToken);
        var (period, dueUtc) = await ClientReportService.GetNextScheduledAsync(context, schedule, ReportSettingsService.ResolveZone(settings.TimeZoneId),
            settings.SendHour, timeProvider.GetUtcNow(), cancellationToken);
        return TypedResults.Ok(new ApiReportSchedule(schedule.Frequency.ToString(), schedule.Recipients, schedule.StartedUtc,
            new ApiReportPeriod(period.Start, period.End, period.Kind.ToString()), dueUtc));
    }

    private static async Task<Results<Ok<ApiPage<ApiReportDelivery>>, ValidationProblem, ProblemHttpResult>> ListDeliveriesAsync(
        [FromRoute(Name = "group")] int groupId, [AsParameters] DeliveryFilters filters, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var statuses = reader.Enum<ClientReportDeliveryStatus>("status", filters.Status);
        var kinds = reader.Enum<ClientReportDeliveryKind>("kind", filters.Kind);
        var sentAfter = reader.Date("sentAfter", filters.SentAfter);
        var sentBefore = reader.Date("sentBefore", filters.SentBefore);
        reader.Range("sentAfter", sentAfter, "sentBefore", sentBefore);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayReachAsync(context, user, groupId, cancellationToken))
        {
            return ApiProblems.NotFound($"group {groupId}");
        }

        var query = context.ClientReportDeliveries.AsNoTracking().Where(delivery => delivery.GroupId == groupId);
        if (statuses.Count > 0) query = query.Where(delivery => statuses.Contains(delivery.Status));
        if (kinds.Count > 0) query = query.Where(delivery => kinds.Contains(delivery.Kind));
        if (sentAfter is { } from) query = query.Where(delivery => delivery.SentUtc != null && delivery.SentUtc >= from);
        if (sentBefore is { } until) query = query.Where(delivery => delivery.SentUtc != null && delivery.SentUtc < until);
        var totalCount = await query.CountAsync(cancellationToken);
        var deliveries = await query.OrderByDescending(delivery => delivery.Id).Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ApiPage<ApiReportDelivery>(deliveries.Select(ApiReportDelivery.From).ToList(), pageNumber, size, totalCount));
    }

    /// <summary>A period from period and of, or start and end, built with ReportPeriods as the Reports dialog builds
    /// them, in the report time zone. Problems go to the reader.</summary>
    private static ReportPeriod? ReadPeriod(ApiFilterReader reader, string? period, string? of, string? start, string? end, TimeZoneInfo zone, DateTimeOffset nowUtc)
    {
        var hasPeriod = !string.IsNullOrWhiteSpace(period);
        var hasStart = !string.IsNullOrWhiteSpace(start);
        var hasEnd = !string.IsNullOrWhiteSpace(end);
        if (hasPeriod && (hasStart || hasEnd))
        {
            reader.Refuse("period", "Use period, or start and end, not both.");
            return null;
        }

        if (!hasPeriod && !string.IsNullOrWhiteSpace(of))
        {
            reader.Refuse("of", "Use of with period, not with start and end.");
            return null;
        }

        if (!hasPeriod && !hasStart && !hasEnd)
        {
            reader.Refuse("period", "Give period (week, month or quarter), or start and end.");
            return null;
        }

        if (!hasPeriod)
        {
            if (!hasStart || !hasEnd)
            {
                reader.Refuse(hasStart ? "end" : "start", "Give both start and end.");
                return null;
            }

            var startDay = Day(reader, "start", start);
            var endDay = Day(reader, "end", end);
            if (startDay is null || endDay is null)
            {
                return null;
            }

            try
            {
                return ReportPeriods.FromRange(startDay.Value, endDay.Value, zone, nowUtc);
            }
            catch (ArgumentException exception)
            {
                reader.Refuse("start", exception.Message.Split(" (Parameter")[0]);
                return null;
            }
        }

        var kind = reader.Single("period", period, PeriodKinds);
        var inside = string.IsNullOrWhiteSpace(of) ? ReportPeriods.LocalDay(nowUtc, zone) : Day(reader, "of", of);
        if (kind is null || inside is not { } day)
        {
            return null;
        }

        if (day > ReportPeriods.LocalDay(nowUtc, zone))
        {
            reader.Refuse("of", "Must be today or earlier: a period that hasn't started has nothing to report.");
            return null;
        }

        return kind switch
        {
            "week" => ReportPeriods.Week(day.AddDays(-(((int)day.DayOfWeek + 6) % 7))),
            "month" => ReportPeriods.Month(new DateOnly(day.Year, day.Month, 1)),
            _ => ReportPeriods.Quarter(new DateOnly(day.Year, ((day.Month - 1) / 3 * 3) + 1, 1)),
        };
    }

    private static DateOnly? Day(ApiFilterReader reader, string name, string? raw)
    {
        if (DateOnly.TryParseExact(raw?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day;
        }

        reader.Refuse(name, "Use a date such as 2026-09-01.");
        return null;
    }

    private static async Task<Results<FileContentHttpResult, ValidationProblem, ProblemHttpResult>> DownloadAsync(
        [FromRoute(Name = "group")] int groupId, string? period, string? of, string? start, string? end, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, ClientReportRunner runner, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayReachAsync(context, user, groupId, cancellationToken))
        {
            return ApiProblems.NotFound($"group {groupId}");
        }

        var reader = new ApiFilterReader();
        var chosen = ReadPeriod(reader, period, of, start, end, await runner.ZoneAsync(cancellationToken), timeProvider.GetUtcNow());
        if (reader.Problem is not null || chosen is null)
        {
            // ReadPeriod records a problem whenever it can't give a period, so the reader always has one here.
            return reader.Problem!;
        }

        return await runner.RenderAsync(groupId, chosen, cancellationToken) is { } file
            ? TypedResults.File(file.Pdf, "application/pdf", file.FileName)
            : ApiProblems.NotFound($"group {groupId}");
    }

    private static async Task<Results<Ok<ApiReportDelivery>, ValidationProblem, ProblemHttpResult>> SendAsync(
        [FromRoute(Name = "group")] int groupId, ApiSendReportRequest? request, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory,
        ClientReportRunner runner, IEmailSenderFactory senderFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayReachAsync(context, user, groupId, cancellationToken))
        {
            return ApiProblems.NotFound($"group {groupId}");
        }

        var reader = new ApiFilterReader();
        var chosen = ReadPeriod(reader, request?.Period, request?.Of, request?.Start, request?.End, await runner.ZoneAsync(cancellationToken), timeProvider.GetUtcNow());
        var recipients = request?.Recipients ?? (await ClientReportService.GetScheduleAsync(context, groupId, cancellationToken))?.Recipients ?? [];
        if (recipients.Any(string.IsNullOrWhiteSpace))
        {
            reader.Refuse("recipients", "Each recipient must be an email address.");
        }
        else
        {
            try
            {
                if (ReportRecipients.Normalise(recipients).Count == 0)
                {
                    reader.Refuse("recipients", "Give recipients: the group has none on its schedule.");
                }
            }
            catch (ArgumentException exception)
            {
                reader.Refuse("recipients", exception.Message.Split(" (Parameter")[0]);
            }
        }

        if (reader.Problem is not null || chosen is null)
        {
            // ReadPeriod records a problem whenever it can't give a period, so the reader always has one here.
            return reader.Problem!;
        }

        if (await senderFactory.GetAsync(cancellationToken) is null)
        {
            return ApiProblems.Conflict("Email isn't set up. Set it up on Email & reports in dotMARC first.");
        }

        // Not cancellable by the caller: an email that has gone out must be recorded and audited even if the caller hangs
        // up, as the Reports dialog ensures.
        var result = await runner.SendNowAsync(AuditActor.FromPrincipal(user), groupId, chosen, recipients, CancellationToken.None);
        if (result.DeliveryId is not { } deliveryId)
        {
            return TypedResults.Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        var delivery = await context.ClientReportDeliveries.AsNoTracking().SingleAsync(candidate => candidate.Id == deliveryId, CancellationToken.None);
        return TypedResults.Ok(ApiReportDelivery.From(delivery));
    }
}
