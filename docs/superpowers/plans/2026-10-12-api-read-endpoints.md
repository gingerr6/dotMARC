# API Read Endpoints Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** API endpoints for sending sources, DMARC and TLS reports (with raw files), the dashboard summary, client reports (schedule, history, PDF, send now), parse failures, and PSA tickets on alerts.

**Architecture:** Each area is its own endpoint class (`SourceEndpoints`, `ReportEndpoints`, `TlsReportEndpoints`, `SummaryEndpoints`, `ClientReportEndpoints`, `ParseFailureEndpoints`) mapped from `ApiEndpoints`, using the first piece's `ApiFilterReader`, `ApiFilterDocs` and `ApiDomainKey`. Totals are computed in the database; only data that has to be classified in C# (acted-on records for the reason breakdown) is loaded. Existing services are reused for the rules: `DashboardSummary`, `PortalStatus`, `IpInfoService`, `ClientReportService`, `ClientReportRunner`, `ReportPeriods`.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 with Npgsql, Microsoft.OpenApi, xUnit with Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-12-api-read-endpoints-design.md`

## Global Constraints

- Filters follow the first piece: lists by commas or repeats, case-insensitive values, dates as a day or a time with a zone, one 400 naming every bad filter, a domain the key can't see is a 404.
- Permissions: Domains view for sources, reports, TLS reports and the summary; Reports manage for client reports; Domains edit for parse failures; Alerts view for tickets. Group-limited keys see only their own data.
- Paging: 50 a page by default, at most 200 (`DomainReadEndpoints.MaximumPageSize`).
- No em dashes in prose, comments or commit messages; meaningful variable names, including in tests.
- Tests run against the demo-seeded `ApiTestHost`; seed uniquely named data and filter by it.
- After any endpoint change, regenerate the committed OpenAPI file with `node scripts/update-openapi.mjs`, and update `OpenApiDocumentTests`: the operation count, the error map in `EveryOperation_DescribesEachErrorItCanReturn_AsAProblem`, and the `sections` array when a new tag is added.
- Test command for a filter: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~<Name>"`; whole suite `dotnet test dotMARC.sln`.

## Review Focus

1. A report or TLS report id that belongs to a different domain, requested under a domain the key can see: must be a 404, never the other domain's report. (Test in Tasks 2 and 3.)
2. A Group-limited key asking for another Group's schedule, deliveries, PDF or send: a 404, and nothing sent. (Test in Task 5.)
3. Send now with recipients given in the body that include an invalid address: a 400 naming `recipients`, and no delivery recorded. (Test in Task 5.)
4. The summary for a Group-limited key: figures over its domains only, and `parseFailureCount` null. (Test in Task 4.)
5. Sources with `organization` and paging: `totalCount` counts only matching sources. (Test in Task 1.)

---

### Task 1: Sending sources, and one shared report range

**Files:**
- Create: `src/DotMarc/Api/SourceEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiFilterReader.cs` (add `ReportRange`), `src/DotMarc/Api/DomainReadEndpoints.cs` (summary uses it), `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiTags.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/SourceEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Produces:
  - `ApiFilterReader.ReportRange(string? days, string? from, string? to, DateTimeOffset nowUtc) -> (DateTimeOffset Start, DateTimeOffset End)`
  - `ApiTags.Reports` = `"Reports"`
  - `ApiSourceDetail(string Ip, int Volume, int Passed, int Failed, double? PassRate, string Spf, string Dkim, string Disposition, string? Organization, string? Country)`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/SourceEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class SourceEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public SourceEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportWith(DateTimeOffset receivedUtc, params (string SourceIp, int Count, AuthResult Spf, AuthResult Dkim, DispositionResult Disposition)[] records)
    {
        var report = new Report
        {
            ReportingOrg = "google.com", ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
            RawXml = "<feedback/>", ReceivedUtc = receivedUtc, AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, count, spf, dkim, disposition) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = count, Disposition = disposition, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    private async Task<int> SeedMixedAsync(string name)
    {
        var received = DateTimeOffset.UtcNow.AddDays(-2);
        return await _host.SeedDomainAsync(name, configure: domain => domain.Reports.Add(ReportWith(received,
            ("192.0.2.30", 50, AuthResult.Pass, AuthResult.Pass, DispositionResult.None),
            ("192.0.2.31", 10, AuthResult.Fail, AuthResult.Fail, DispositionResult.Quarantine),
            ("192.0.2.31", 4, AuthResult.Pass, AuthResult.Fail, DispositionResult.None),
            ("192.0.2.32", 6, AuthResult.Fail, AuthResult.Fail, DispositionResult.Reject))));
    }

    [Fact]
    public async Task Sources_MatchDomainStatistics_BusiestFirst()
    {
        var domainId = await SeedMixedAsync("api-sources.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources.example/sources");

        await using var context = _host.CreateContext();
        var reports = await context.Reports.Where(report => report.DomainId == domainId).Include(report => report.Records).ToListAsync();
        var expected = DomainStatistics.GetSourceAggregates(reports).OrderByDescending(source => source.Volume).ThenBy(source => source.SourceIp)
            .Select(source => (source.SourceIp, source.Volume, source.SpfResult.ToString(), source.DkimResult.ToString(), source.Disposition.ToString()));
        Assert.Equal(expected, page!.Items.Select(source => (source.Ip, source.Volume, source.Spf, source.Dkim, source.Disposition)));
        var mixed = page.Items.Single(source => source.Ip == "192.0.2.31");
        Assert.Equal((14, 4, 10), (mixed.Volume, mixed.Passed, mixed.Failed));
        Assert.Equal(4d / 14, mixed.PassRate!.Value, precision: 6);
    }

    [Fact]
    public async Task Filters_NarrowSources()
    {
        await SeedMixedAsync("api-sources-filter.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);
        async Task<IEnumerable<string>> IpsFor(string query) =>
            (await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>($"/api/v1/domains/api-sources-filter.example/sources?{query}"))!.Items.Select(source => source.Ip);

        Assert.Equal(["192.0.2.30"], await IpsFor("result=passing"));
        Assert.Equal(["192.0.2.31", "192.0.2.32"], await IpsFor("result=FAILING"));
        Assert.Equal(["192.0.2.32"], await IpsFor("disposition=reject"));
        Assert.Equal(["192.0.2.31"], await IpsFor("disposition=quarantine&spf=pass"));
        Assert.Equal(["192.0.2.30", "192.0.2.31"], await IpsFor("minVolume=10"));
        Assert.Equal(["192.0.2.32"], await IpsFor("ip=.32"));
    }

    [Fact]
    public async Task Organization_ComesFromTheCache_AndFilters_WithPagingCountingMatches()
    {
        await SeedMixedAsync("api-sources-org.example");
        await using (var context = _host.CreateContext())
        {
            context.IpInfos.Add(new IpInfo { Ip = "192.0.2.30", Organization = "Contoso Mail", Country = "GB", Status = IpLookupStatus.Ok, LookedUpUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources-org.example/sources");
        var contoso = await client.GetFromJsonAsync<ApiPage<ApiSourceDetail>>("/api/v1/domains/api-sources-org.example/sources?organization=contoso&pageSize=1");

        Assert.Equal(("Contoso Mail", "GB"), (all!.Items.Single(source => source.Ip == "192.0.2.30").Organization, all.Items.Single(source => source.Ip == "192.0.2.30").Country));
        Assert.Null(all.Items.Single(source => source.Ip == "192.0.2.31").Organization);
        Assert.Equal(1, contoso!.TotalCount);
        Assert.Equal(["192.0.2.30"], contoso.Items.Select(source => source.Ip));
    }

    [Fact]
    public async Task BadFilters_AndAnUnseenDomain()
    {
        await SeedMixedAsync("api-sources-bad.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var bad = await client.GetAsync("/api/v1/domains/api-sources-bad.example/sources?result=maybe&days=7&from=2026-01-01");
        var unseen = await client.GetAsync("/api/v1/domains/nowhere-at-all.example/sources");

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("\"result\"", body);
        Assert.Contains("\"days\"", body);
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }
}
```

In `OpenApiDocumentTests`: change the operation count from `13` to `14`, add `["GET /api/v1/domains/{domain}/sources"] = [400, 404],` to the error map, and add `"Reports"` to `sections`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SourceEndpointTests"`
Expected: build FAIL, `ApiSourceDetail` is not defined.

- [ ] **Step 3: The shared report range**

In `ApiFilterReader.cs` (add `using DotMarc.Reporting;`), add:

```csharp
    /// <summary>The report window: the last 1 to 30 days (default 30), or from and to for up to 366 days, not both. to
    /// defaults to now; a from that isn't before it is refused.</summary>
    public (DateTimeOffset Start, DateTimeOffset End) ReportRange(string? days, string? from, string? to, DateTimeOffset nowUtc)
    {
        var maximumDays = (int)DomainStatistics.ReportWindow.TotalDays;
        var windowDays = Integer("days", days, 1, maximumDays);
        var rangeStart = Date("from", from);
        var rangeEnd = Date("to", to);
        Range("from", rangeStart, "to", rangeEnd);
        if (!string.IsNullOrWhiteSpace(days) && (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to)))
        {
            Fail("days", "Use days, or from and to, not both.");
        }

        var endUtc = rangeEnd ?? nowUtc;
        var startUtc = rangeStart ?? endUtc.AddDays(-(windowDays ?? maximumDays));
        if (rangeEnd is null && startUtc >= endUtc)
        {
            Fail("from", "Must be earlier than to, which is now when it isn't given.");
        }
        else if ((endUtc - startUtc).TotalDays > 366)
        {
            Fail("from", "The range can be at most 366 days.");
        }

        return (startUtc, endUtc);
    }
```

In `DomainReadEndpoints.GetReportSummaryAsync`, replace everything from `var maximumDays = ...` down to the `if (reader.Problem is { } problem)` block's opening (the days/from/to/days-with-dates/start/end checks) with:

```csharp
        var sourceCount = reader.Integer("top", top, 1, 200) ?? TopSourceCount;
        var (startUtc, endUtc) = reader.ReportRange(days, from, to, timeProvider.GetUtcNow());
```

keeping the `if (reader.Problem is { } problem) return problem;` that follows. The existing summary tests must pass unchanged.

- [ ] **Step 4: Models, section and endpoint**

Append to `ApiModels.cs`:

```csharp
/// <summary>A sending IP over the range. Passed means SPF or DKIM passed; Spf and Dkim are Pass if they passed in any
/// record; Disposition is the most severe seen. Organization and Country come from dotMARC's cache of IP lookups.</summary>
public sealed record ApiSourceDetail(string Ip, int Volume, int Passed, int Failed, double? PassRate, string Spf, string Dkim, string Disposition,
    string? Organization, string? Country);
```

In `ApiTags`, add `public const string Reports = "Reports";` and `(Reports, "Sending sources, DMARC and TLS reports with their original files, and the dashboard's headline figures."),`. In `ApiEndpoints.MapDotMarcApi`, add `SourceEndpoints.Map(api);` after `DomainReadEndpoints.Map(api);`.

`src/DotMarc/Api/SourceEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.IpEnrichment;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;

namespace DotMarc.Api;

public static class SourceEndpoints
{
    private const int DefaultPageSize = 50;

    public sealed class SourceFilters
    {
        [FromQuery(Name = "days")] public string? Days { get; set; }
        [FromQuery(Name = "from")] public string? From { get; set; }
        [FromQuery(Name = "to")] public string? To { get; set; }
        [FromQuery(Name = "result")] public string? Result { get; set; }
        [FromQuery(Name = "spf")] public string? Spf { get; set; }
        [FromQuery(Name = "dkim")] public string? Dkim { get; set; }
        [FromQuery(Name = "disposition")] public string[]? Disposition { get; set; }
        [FromQuery(Name = "minVolume")] public string? MinVolume { get; set; }
        [FromQuery(Name = "ip")] public string? Ip { get; set; }
        [FromQuery(Name = "organization")] public string? Organization { get; set; }
        [FromQuery(Name = "page")] public string? Page { get; set; }
        [FromQuery(Name = "pageSize")] public string? PageSize { get; set; }
    }

    private static readonly IReadOnlyList<string> Results = ["passing", "failing"];
    private static readonly IReadOnlyList<string> PassOrFail = ["Pass", "Fail"];

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/domains/{domain}/sources", ListSourcesAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("ListSources")
            .WithSummary("List a domain's sending sources")
            .WithDescription($"Every IP that sent as the domain over the range, busiest first, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine. {ApiDomainKey.Description}")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("days", "The last 1 to 30 days (default 30). Use this or from and to.", type: JsonSchemaType.Integer)
            .DocumentsFilter("from", "Start of the range: a date (2026-10-01) or time with a zone, included. At most 366 days before to.")
            .DocumentsFilter("to", "End of the range: a date or time, not included. Defaults to now.")
            .DocumentsFilter("result", "passing for sources with no failed messages, failing for those with any.", Results)
            .DocumentsFilter("spf", "Pass for sources whose SPF passed at least once, Fail for the rest.", PassOrFail)
            .DocumentsFilter("dkim", "Pass for sources whose DKIM passed at least once, Fail for the rest.", PassOrFail)
            .DocumentsFilter("disposition", "The most severe action receivers took." + ApiFilterDocs.ListHint, Enum.GetNames<DispositionResult>())
            .DocumentsFilter("minVolume", "At least this many messages.", type: JsonSchemaType.Integer)
            .DocumentsFilter("ip", "Part of the IP address.")
            .DocumentsFilter("organization", "Part of the network owner's name, in any letter case. IPs dotMARC hasn't looked up yet never match.")
            .DocumentsFilter("page", "Which page, from 1 (default 1).", type: JsonSchemaType.Integer)
            .DocumentsFilter("pageSize", $"How many a page, 1 to {DomainReadEndpoints.MaximumPageSize} (default {DefaultPageSize}).", type: JsonSchemaType.Integer);
    }

    private static async Task<Results<Ok<ApiPage<ApiSourceDetail>>, ValidationProblem, ProblemHttpResult>> ListSourcesAsync(
        [FromRoute(Name = "domain")] string domainKey, [AsParameters] SourceFilters filters, ClaimsPrincipal user,
        IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var (startUtc, endUtc) = reader.ReportRange(filters.Days, filters.From, filters.To, timeProvider.GetUtcNow());
        var result = reader.Single("result", filters.Result, Results);
        var spf = reader.Single("spf", filters.Spf, PassOrFail);
        var dkim = reader.Single("dkim", filters.Dkim, PassOrFail);
        var dispositions = reader.Enum<DispositionResult>("disposition", filters.Disposition);
        var minVolume = reader.Integer("minVolume", filters.MinVolume, 1, int.MaxValue);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domainId = await ApiDomainKey.Matching(ApiScope.From(user).Domains(context.Domains.AsNoTracking()), domainKey)
            .Select(domain => (int?)domain.Id).SingleOrDefaultAsync(cancellationToken);
        if (domainId is null)
        {
            return ApiProblems.NotFound($"domain {domainKey}");
        }

        var records = context.ReportRecords.AsNoTracking()
            .Where(record => record.Report.DomainId == domainId && record.Report.ReceivedUtc >= startUtc && record.Report.ReceivedUtc < endUtc);
        if (LikePattern.Contains(filters.Ip) is { } ipPattern)
        {
            records = records.Where(record => EF.Functions.ILike(record.SourceIp, ipPattern, LikePattern.Escape));
        }

        var sources = records
            .GroupBy(record => record.SourceIp)
            .Select(grouping => new SourceTotals
            {
                Ip = grouping.Key,
                Volume = grouping.Sum(record => record.MessageCount),
                Passed = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? record.MessageCount : 0),
                SpfPasses = grouping.Sum(record => record.SpfResult == AuthResult.Pass ? 1 : 0),
                DkimPasses = grouping.Sum(record => record.DkimResult == AuthResult.Pass ? 1 : 0),
                Rejected = grouping.Sum(record => record.Disposition == DispositionResult.Reject ? 1 : 0),
                Quarantined = grouping.Sum(record => record.Disposition == DispositionResult.Quarantine ? 1 : 0),
            });
        if (result == "passing") sources = sources.Where(source => source.Passed == source.Volume);
        if (result == "failing") sources = sources.Where(source => source.Passed < source.Volume);
        if (spf is not null) sources = spf == "Pass" ? sources.Where(source => source.SpfPasses > 0) : sources.Where(source => source.SpfPasses == 0);
        if (dkim is not null) sources = dkim == "Pass" ? sources.Where(source => source.DkimPasses > 0) : sources.Where(source => source.DkimPasses == 0);
        if (dispositions.Count > 0)
        {
            var reject = dispositions.Contains(DispositionResult.Reject);
            var quarantine = dispositions.Contains(DispositionResult.Quarantine);
            var none = dispositions.Contains(DispositionResult.None);
            sources = sources.Where(source => (reject && source.Rejected > 0)
                || (quarantine && source.Rejected == 0 && source.Quarantined > 0)
                || (none && source.Rejected == 0 && source.Quarantined == 0));
        }

        if (minVolume is { } atLeast) sources = sources.Where(source => source.Volume >= atLeast);
        var ordered = sources.OrderByDescending(source => source.Volume).ThenBy(source => source.Ip);

        // An organization filter matches cached names, so it filters after the totals; otherwise page in the database.
        List<SourceTotals> pageRows;
        int totalCount;
        Func<string, (string? Organization, string? Country)> owner;
        if (LikePattern.Contains(filters.Organization) is not null)
        {
            var all = await ordered.ToListAsync(cancellationToken);
            owner = await OwnersAsync(context, all.Select(source => source.Ip).ToList(), timeProvider, cancellationToken);
            var needle = filters.Organization!.Trim();
            var matching = all.Where(source => owner(source.Ip).Organization?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true).ToList();
            totalCount = matching.Count;
            pageRows = matching.Skip((pageNumber - 1) * size).Take(size).ToList();
        }
        else
        {
            totalCount = await ordered.CountAsync(cancellationToken);
            pageRows = await ordered.Skip((pageNumber - 1) * size).Take(size).ToListAsync(cancellationToken);
            owner = await OwnersAsync(context, pageRows.Select(source => source.Ip).ToList(), timeProvider, cancellationToken);
        }

        var items = pageRows.Select(source =>
        {
            var (organization, country) = owner(source.Ip);
            return new ApiSourceDetail(source.Ip, source.Volume, source.Passed, source.Volume - source.Passed,
                source.Volume == 0 ? null : (double)source.Passed / source.Volume,
                source.SpfPasses > 0 ? "Pass" : "Fail", source.DkimPasses > 0 ? "Pass" : "Fail",
                (source.Rejected > 0 ? DispositionResult.Reject : source.Quarantined > 0 ? DispositionResult.Quarantine : DispositionResult.None).ToString(),
                organization, country);
        }).ToList();
        return TypedResults.Ok(new ApiPage<ApiSourceDetail>(items, pageNumber, size, totalCount));
    }

    /// <summary>The network owner for each IP from dotMARC's lookup cache, as the domain page shows it: the IP's own
    /// result while it's current, else a cached range containing it, else unknown. Never looks anything up.</summary>
    private static async Task<Func<string, (string? Organization, string? Country)>> OwnersAsync(DotMarcDbContext context, List<string> ips,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var cached = await IpInfoService.GetCachedAsync(context, ips, cancellationToken);
        var ranges = await IpInfoService.GetCachedRangesAsync(context, cancellationToken);
        var nowUtc = timeProvider.GetUtcNow();
        return ip =>
        {
            if (cached.TryGetValue(ip, out var info) && !IpInfoService.NeedsLookup(info, nowUtc))
            {
                return (info.Organization, info.Country);
            }

            return IpRangeMatcher.FindContaining(ranges, ip) is { } range ? (range.Organization, range.Country) : (null, null);
        };
    }

    private sealed class SourceTotals
    {
        public string Ip { get; init; } = "";
        public int Volume { get; init; }
        public int Passed { get; init; }
        public int SpfPasses { get; init; }
        public int DkimPasses { get; init; }
        public int Rejected { get; init; }
        public int Quarantined { get; init; }
    }
}
```

If EF can't translate a `Where` over the grouped projection's properties, rule on the smallest change (for example filtering with `HAVING`-style conditions written against `grouping.Sum(...)` inline) and ledger it.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SourceEndpointTests|FullyQualifiedName~DomainReadEndpointTests|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS except `TheCommittedCopy_MatchesTheApp`; run `node scripts/update-openapi.mjs` and re-run: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add a domain's sending sources to the API, totalled in the database"
```

---

### Task 2: DMARC reports, one report and its raw XML

**Files:**
- Create: `src/DotMarc/Api/ReportEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/ReportEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Produces: `ApiDmarcReport`, `ApiDmarcRecord`, `ApiOverrideReason`, `ApiAuthDetail` records (below); `ReportEndpoints.ReportFilters`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/ReportEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static Report ReportFrom(string org, DateTimeOffset receivedUtc, string xml, params (string SourceIp, int Count, AuthResult Spf, AuthResult Dkim)[] records)
    {
        var report = new Report
        {
            ReportingOrg = org, ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
            RawXml = xml, ReceivedUtc = receivedUtc, AuthDetailBackfilledUtc = receivedUtc,
        };
        foreach (var (sourceIp, count, spf, dkim) in records)
        {
            report.Records.Add(new ReportRecord { SourceIp = sourceIp, MessageCount = count, Disposition = DispositionResult.None, SpfResult = spf, DkimResult = dkim, HeaderFrom = "example" });
        }

        return report;
    }

    [Fact]
    public async Task Reports_ComeNewestFirst_WithRecordsAndDetails_AndFilter()
    {
        await _host.SeedDomainAsync("api-reports.example", configure: domain =>
        {
            var google = ReportFrom("google.com", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), "<feedback>g</feedback>", ("192.0.2.40", 8, AuthResult.Pass, AuthResult.Pass));
            google.Records[0].AuthDetails.Add(new ReportRecordAuthDetail { Mechanism = DmarcAuthMechanism.Dkim, Domain = "api-reports.example", Result = DmarcMechanismResult.Pass, Selector = "s1" });
            domain.Reports.Add(google);
            domain.Reports.Add(ReportFrom("Microsoft Corporation", new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero), "<feedback>m</feedback>",
                ("192.0.2.41", 2, AuthResult.Fail, AuthResult.Fail), ("192.0.2.42", 6, AuthResult.Pass, AuthResult.Fail)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports");
        var failing = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports?failingOnly=true");
        var byOrg = await client.GetFromJsonAsync<ApiPage<ApiDmarcReport>>("/api/v1/domains/api-reports.example/reports?reportingOrg=GOOGLE&receivedBefore=2026-09-02");

        Assert.Equal(["Microsoft Corporation", "google.com"], all!.Items.Select(report => report.ReportingOrg));
        Assert.Equal((8, 6d / 8), (all.Items[0].MessageCount, all.Items[0].PassRate!.Value));
        var detail = Assert.Single(all.Items[1].Records).AuthDetails.Single();
        Assert.Equal(("Dkim", "Pass", "s1"), (detail.Mechanism, detail.Result, detail.Selector));
        Assert.Equal(["Microsoft Corporation"], failing!.Items.Select(report => report.ReportingOrg));
        Assert.Equal(["google.com"], byOrg!.Items.Select(report => report.ReportingOrg));
    }

    [Fact]
    public async Task OneReport_AndItsRawXml_AndAnotherDomainsIdIsA404()
    {
        await _host.SeedDomainAsync("api-report-one.example", configure: domain =>
            domain.Reports.Add(ReportFrom("google.com", DateTimeOffset.UtcNow, "<feedback><raw/></feedback>", ("192.0.2.43", 1, AuthResult.Pass, AuthResult.Pass))));
        await _host.SeedDomainAsync("api-report-other.example", configure: domain =>
            domain.Reports.Add(ReportFrom("yahoo.com", DateTimeOffset.UtcNow, "<feedback/>", ("192.0.2.44", 1, AuthResult.Pass, AuthResult.Pass))));
        await using var context = _host.CreateContext();
        var reportId = await context.Reports.Where(report => report.Domain.Name == "api-report-one.example").Select(report => report.Id).SingleAsync();
        var otherId = await context.Reports.Where(report => report.Domain.Name == "api-report-other.example").Select(report => report.Id).SingleAsync();
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var one = await client.GetFromJsonAsync<ApiDmarcReport>($"/api/v1/domains/api-report-one.example/reports/{reportId}");
        var raw = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{reportId}/raw");
        var crossed = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{otherId}");
        var crossedRaw = await client.GetAsync($"/api/v1/domains/api-report-one.example/reports/{otherId}/raw");

        Assert.Equal(reportId, one!.Id);
        Assert.Equal("application/xml", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<feedback><raw/></feedback>", await raw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, crossedRaw.StatusCode);
    }
}
```

In `OpenApiDocumentTests`: count `14` to `17`; add to the error map `["GET /api/v1/domains/{domain}/reports"] = [400, 404], ["GET /api/v1/domains/{domain}/reports/{id}"] = [404], ["GET /api/v1/domains/{domain}/reports/{id}/raw"] = [404],`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ReportEndpointTests"`
Expected: build FAIL, `ApiDmarcReport` is not defined.

- [ ] **Step 3: Models**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiOverrideReason(string Type, string? Comment);

public sealed record ApiAuthDetail(string Mechanism, string Domain, string Result, string? Selector, string? Scope);

public sealed record ApiDmarcRecord(string SourceIp, int Count, string Disposition, string Spf, string Dkim, string HeaderFrom,
    IReadOnlyList<ApiOverrideReason> OverrideReasons, IReadOnlyList<ApiAuthDetail> AuthDetails);

public sealed record ApiDmarcReport(int Id, string ReportingOrg, string ReportId, DateTimeOffset DateRangeBeginUtc, DateTimeOffset DateRangeEndUtc,
    DateTimeOffset ReceivedUtc, int MessageCount, double? PassRate, IReadOnlyList<ApiDmarcRecord> Records)
{
    /// <summary>Needs Records with OverrideReasons and AuthDetails loaded.</summary>
    public static ApiDmarcReport From(Report report)
    {
        var total = report.Records.Sum(record => record.MessageCount);
        var passing = report.Records.Where(DotMarc.Reporting.DomainStatistics.IsPassing).Sum(record => record.MessageCount);
        return new ApiDmarcReport(report.Id, report.ReportingOrg, report.ReportId, report.DateRangeBeginUtc, report.DateRangeEndUtc, report.ReceivedUtc,
            total, total == 0 ? null : (double)passing / total,
            report.Records.Select(record => new ApiDmarcRecord(record.SourceIp, record.MessageCount, record.Disposition.ToString(), record.SpfResult.ToString(),
                record.DkimResult.ToString(), record.HeaderFrom,
                record.OverrideReasons.Select(reason => new ApiOverrideReason(reason.Type.ToString(), reason.Comment)).ToList(),
                record.AuthDetails.Select(detail => new ApiAuthDetail(detail.Mechanism.ToString(), detail.Domain, detail.Result.ToString(), detail.Selector, detail.Scope)).ToList()))
            .ToList());
    }
}
```

- [ ] **Step 4: Endpoints**

`src/DotMarc/Api/ReportEndpoints.cs`:

```csharp
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
```

In `ApiEndpoints.MapDotMarcApi`, add `ReportEndpoints.Map(api);` after `SourceEndpoints.Map(api);`. If Npgsql can't translate `orgPatterns.Any(... ILike ...)`, combine one `ILike` per pattern with `||` built from an expression, and ledger the ruling.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ReportEndpointTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~DomainReadEndpointTests"`
Expected: PASS after `node scripts/update-openapi.mjs`. (`/reports/summary` still routes to the summary, since `summary` isn't an `int`.)

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add DMARC reports, one report and its original XML to the API"
```

---

### Task 3: TLS reports, one report and its raw JSON

**Files:**
- Create: `src/DotMarc/Api/TlsReportEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/TlsReportEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Produces: `ApiTlsReport`, `ApiTlsPolicy`, `ApiTlsFailure` records

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/TlsReportEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class TlsReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public TlsReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private static TlsrptReport TlsReport(string org, DateTimeOffset receivedUtc, long successful, long failed, string? failureType = null) => new()
    {
        ReportingOrg = org, ReportId = Guid.NewGuid().ToString(), DateRangeBeginUtc = receivedUtc.AddDays(-1), DateRangeEndUtc = receivedUtc,
        RawJson = $$"""{"organization-name":"{{org}}"}""", ReceivedUtc = receivedUtc,
        Policies =
        [
            new TlsrptReportPolicy
            {
                PolicyType = "sts", PolicyDomain = "api-tls.example", SuccessfulSessionCount = successful, FailedSessionCount = failed,
                FailureDetails = failureType is null ? [] : [new TlsrptFailureDetail { ResultType = failureType, FailedSessionCount = failed, ReceivingMxHostname = "mx.api-tls.example" }],
            },
        ],
    };

    [Fact]
    public async Task TlsReports_ComeNewestFirst_AndFilter()
    {
        var domainId = await _host.SeedDomainAsync("api-tls.example");
        await using (var context = _host.CreateContext())
        {
            context.TlsrptReports.Add(Attach(TlsReport("google.com", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), 40, 0), domainId));
            context.TlsrptReports.Add(Attach(TlsReport("Microsoft", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), 30, 3, "certificate-expired"), domainId));
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports");
        var failures = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports?failuresOnly=true");
        var expired = await client.GetFromJsonAsync<ApiPage<ApiTlsReport>>("/api/v1/domains/api-tls.example/tls-reports?resultType=CERTIFICATE-EXPIRED");

        Assert.Equal(["Microsoft", "google.com"], all!.Items.Select(report => report.ReportingOrg));
        Assert.Equal((30L, 3L), (all.Items[0].SuccessfulSessions, all.Items[0].FailedSessions));
        Assert.Equal("mx.api-tls.example", all.Items[0].Policies.Single().Failures.Single().ReceivingMx);
        Assert.Equal(["Microsoft"], failures!.Items.Select(report => report.ReportingOrg));
        Assert.Equal(["Microsoft"], expired!.Items.Select(report => report.ReportingOrg));
    }

    private static TlsrptReport Attach(TlsrptReport report, int domainId)
    {
        report.DomainId = domainId;
        return report;
    }

    [Fact]
    public async Task OneTlsReport_AndItsRawJson_AndAnotherDomainsIdIsA404()
    {
        var domainId = await _host.SeedDomainAsync("api-tls-one.example");
        var otherId = await _host.SeedDomainAsync("api-tls-other.example");
        int reportId, otherReportId;
        await using (var context = _host.CreateContext())
        {
            var report = Attach(TlsReport("google.com", DateTimeOffset.UtcNow, 5, 0), domainId);
            var other = Attach(TlsReport("yahoo.com", DateTimeOffset.UtcNow, 5, 0), otherId);
            context.TlsrptReports.AddRange(report, other);
            await context.SaveChangesAsync();
            (reportId, otherReportId) = (report.Id, other.Id);
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var one = await client.GetFromJsonAsync<ApiTlsReport>($"/api/v1/domains/api-tls-one.example/tls-reports/{reportId}");
        var raw = await client.GetAsync($"/api/v1/domains/api-tls-one.example/tls-reports/{reportId}/raw");
        var crossed = await client.GetAsync($"/api/v1/domains/api-tls-one.example/tls-reports/{otherReportId}/raw");

        Assert.Equal(reportId, one!.Id);
        Assert.Equal("application/json", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"organization-name":"google.com"}""", await raw.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, crossed.StatusCode);
    }
}
```

In `OpenApiDocumentTests`: count `17` to `20`; add `["GET /api/v1/domains/{domain}/tls-reports"] = [400, 404], ["GET /api/v1/domains/{domain}/tls-reports/{id}"] = [404], ["GET /api/v1/domains/{domain}/tls-reports/{id}/raw"] = [404],`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TlsReportEndpointTests"`
Expected: build FAIL, `ApiTlsReport` is not defined.

- [ ] **Step 3: Models**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiTlsFailure(string ResultType, long Sessions, string? ReceivingMx, string? ReasonCode, string? AdditionalInformation);

public sealed record ApiTlsPolicy(string Type, string Domain, long SuccessfulSessions, long FailedSessions, IReadOnlyList<ApiTlsFailure> Failures);

public sealed record ApiTlsReport(int Id, string ReportingOrg, string ReportId, DateTimeOffset DateRangeBeginUtc, DateTimeOffset DateRangeEndUtc,
    DateTimeOffset ReceivedUtc, long SuccessfulSessions, long FailedSessions, IReadOnlyList<ApiTlsPolicy> Policies)
{
    /// <summary>Needs Policies with FailureDetails loaded.</summary>
    public static ApiTlsReport From(TlsrptReport report) => new(
        report.Id, report.ReportingOrg, report.ReportId, report.DateRangeBeginUtc, report.DateRangeEndUtc, report.ReceivedUtc,
        report.Policies.Sum(policy => policy.SuccessfulSessionCount), report.Policies.Sum(policy => policy.FailedSessionCount),
        report.Policies.Select(policy => new ApiTlsPolicy(policy.PolicyType, policy.PolicyDomain, policy.SuccessfulSessionCount, policy.FailedSessionCount,
            policy.FailureDetails.Select(failure => new ApiTlsFailure(failure.ResultType, failure.FailedSessionCount, failure.ReceivingMxHostname,
                failure.FailureReasonCode, failure.AdditionalInformation)).ToList())).ToList());
}
```

- [ ] **Step 4: Endpoints**

`src/DotMarc/Api/TlsReportEndpoints.cs`:

```csharp
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
```

In `ApiEndpoints.MapDotMarcApi`, add `TlsReportEndpoints.Map(api);` after `ReportEndpoints.Map(api);`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~TlsReportEndpointTests|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS after `node scripts/update-openapi.mjs`.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add TLS reports, one report and its original JSON to the API"
```

---

### Task 4: The dashboard summary

**Files:**
- Create: `src/DotMarc/Api/SummaryEndpoints.cs`
- Modify: `src/DotMarc/Reporting/DashboardSummary.cs` (extract `StatusOf`), `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/SummaryEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `DomainReadEndpoints.PassRatesAsync`, `PortalData.OpenAlertsAsync`, `PortalStatus.For`, `DomainStatistics.GetReasonBreakdown(IEnumerable<ReportRecord>)`
- Produces: `DashboardSummary.StatusOf(Domain domain, double? passRate) -> string` (`"Missing"`, `"Warning"` or `"OK"`); `ApiSummary`, `ApiStatusCounts`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/SummaryEndpointTests.cs`:

```csharp
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class SummaryEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public SummaryEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task TheSummary_MatchesTheDashboard_ForEveryDomainAKeyCanSee()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView, Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary");

        await using var context = _host.CreateContext();
        var cutoff = DomainStatistics.GetWindowCutoffUtc();
        var domains = await context.Domains.AsNoTracking()
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.OverrideReasons)
            .Include(domain => domain.Reports.Where(report => report.ReceivedUtc >= cutoff)).ThenInclude(report => report.Records).ThenInclude(record => record.AuthDetails)
            .AsSplitQuery().ToListAsync();
        var (expected, _) = DashboardSummary.Build(domains, await context.ParseFailures.CountAsync());
        Assert.Equal((expected.DomainCount, expected.WarningCount, expected.MissingCount, expected.SourceCount, expected.ParseFailureCount),
            (summary!.DomainCount, summary.WarningCount, summary.MissingCount, summary.SourceCount, summary.ParseFailureCount!.Value));
        Assert.Equal(expected.OverallPassRate, summary.OverallPassRate, precision: 6);
        Assert.Equal(expected.ReasonBreakdown.BenignOverride, summary.ReasonBreakdown.BenignOverride);
        var openAlerts = (await context.AlertEvents.AsNoTracking().Where(alert => !alert.IsResolved).ToListAsync()).ToLookup(alert => alert.DomainName);
        var healths = domains.Select(domain => PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts[domain.Name].ToList()).Health).ToList();
        Assert.Equal(new ApiStatusCounts(healths.Count(health => health == PortalHealth.Protected), healths.Count(health => health == PortalHealth.MonitoringOnly),
            healths.Count(health => health == PortalHealth.NeedsAttention), healths.Count(health => health == PortalHealth.NoReportsYet)), summary.StatusCounts);
    }

    [Fact]
    public async Task AGroupLimitedKey_GetsItsOwnFigures_AndNoParseFailureCount()
    {
        var group = await _host.SeedGroupAsync("api-summary-own");
        await _host.SeedDomainAsync("api-summary-own1.example", groupIds: [group]);
        await _host.SeedDomainAsync("api-summary-own2.example", groupIds: [group]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView, Permission.DomainsEdit], scopedGroupIds: [group]);
        using var client = _host.ClientFor(secret);

        var summary = await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary");

        Assert.Equal(2, summary!.DomainCount);
        Assert.Null(summary.ParseFailureCount);
        Assert.Equal(2, summary.StatusCounts.NoReportsYet);
    }

    [Fact]
    public async Task WithoutDomainsEdit_ThereIsNoParseFailureCount()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        Assert.Null((await client.GetFromJsonAsync<ApiSummary>("/api/v1/summary"))!.ParseFailureCount);
    }
}
```

In `OpenApiDocumentTests`: count `20` to `21`; add `["GET /api/v1/summary"] = [],`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SummaryEndpointTests"`
Expected: build FAIL, `ApiSummary` is not defined.

- [ ] **Step 3: One status rule**

In `DashboardSummary.cs`, add:

```csharp
    /// <summary>The dashboard's per-domain status: Missing when a monitored domain that sends mail has had no report for
    /// two days, Warning when its pass rate is under 95%, otherwise OK. Shared with the API's summary.</summary>
    public static string StatusOf(Domain domain, double? passRate)
    {
        var isNullRouted = domain.SpfCheckStatus == SpfCheckStatus.NullSpf;
        var missingReport = domain.IsMonitored && !isNullRouted && (domain.LastReportReceivedUtc is null || domain.LastReportReceivedUtc < DateTimeOffset.UtcNow.AddDays(-2));
        return missingReport ? "Missing" : passRate is null or >= 0.95 ? "OK" : "Warning";
    }
```

and in `Build`'s row projection replace the `isNullRouted`, `missingReport` and `status` lines with `var status = StatusOf(d, passRate);`. The existing dashboard tests must pass unchanged.

- [ ] **Step 4: Models and endpoint**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiStatusCounts(int Protected, int MonitoringOnly, int NeedsAttention, int NoReportsYet);

public sealed record ApiSummary(int DomainCount, double OverallPassRate, int WarningCount, int MissingCount, int SourceCount,
    ApiReasonBreakdown ReasonBreakdown, int? ParseFailureCount, ApiStatusCounts StatusCounts);
```

`src/DotMarc/Api/SummaryEndpoints.cs`:

```csharp
using System.Security.Claims;
using DotMarc.Data;
using DotMarc.Portal;
using DotMarc.Reporting;
using DotMarc.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Api;

public static class SummaryEndpoints
{
    public static void Map(RouteGroupBuilder api) =>
        api.MapGet("/summary", GetSummaryAsync)
            .RequirePermission(Permission.DomainsView)
            .WithTags(ApiTags.Reports)
            .WithName("GetSummary")
            .WithSummary("Get the dashboard's headline figures")
            .WithDescription("Over the domains this key can see: how many, the overall 30-day pass rate, warnings, missing reports, reporters, why failing mail was let through or rejected, and how many domains are in each client portal status. parseFailureCount is only for keys with DomainsEdit not limited to certain groups.");

    private static async Task<Ok<ApiSummary>> GetSummaryAsync(ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var domains = await scope.Domains(context.Domains.AsNoTracking()).ToListAsync(cancellationToken);
        var ids = domains.Select(domain => domain.Id).ToList();
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        var passRates = await DomainReadEndpoints.PassRatesAsync(context, ids, cutoffUtc, cancellationToken);
        var records = context.ReportRecords.AsNoTracking().Where(record => ids.Contains(record.Report.DomainId) && record.Report.ReceivedUtc >= cutoffUtc);
        var totals = await records.GroupBy(record => 1)
            .Select(grouping => new
            {
                Total = grouping.Sum(record => (long)record.MessageCount),
                Passing = grouping.Sum(record => record.SpfResult == AuthResult.Pass || record.DkimResult == AuthResult.Pass ? (long)record.MessageCount : 0L),
            })
            .SingleOrDefaultAsync(cancellationToken);
        var sourceCount = await context.Reports.AsNoTracking().Where(report => ids.Contains(report.DomainId) && report.ReceivedUtc >= cutoffUtc)
            .Select(report => report.ReportingOrg).Distinct().CountAsync(cancellationToken);
        var actedOn = await records.Where(record => record.Disposition != DispositionResult.None)
            .Include(record => record.OverrideReasons).Include(record => record.AuthDetails).AsSplitQuery().ToListAsync(cancellationToken);
        var openAlerts = await PortalData.OpenAlertsAsync(context, domains.Select(domain => domain.Name).ToList(), cancellationToken);

        var statuses = domains.Select(domain => DashboardSummary.StatusOf(domain, passRates.TryGetValue(domain.Id, out var rate) ? rate : null)).ToList();
        var healths = domains.Select(domain => PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts[domain.Name].ToList()).Health).ToList();
        var breakdown = DomainStatistics.GetReasonBreakdown(actedOn);
        var mayCountParseFailures = !scope.IsScoped && user.HasClaim(UserAccessClaimsTransformation.PermissionClaimType, nameof(Permission.DomainsEdit));
        return TypedResults.Ok(new ApiSummary(
            domains.Count,
            totals is { Total: > 0 } ? (double)totals.Passing / totals.Total : 0,
            statuses.Count(status => status == "Warning"),
            statuses.Count(status => status == "Missing"),
            sourceCount,
            new ApiReasonBreakdown(breakdown.BenignOverride, breakdown.LocalPolicy, breakdown.Other, breakdown.NoReasonGiven,
                breakdown.InferredSpfFailure, breakdown.InferredDkimFailure, breakdown.InferredBothFailure),
            mayCountParseFailures ? await context.ParseFailures.CountAsync(cancellationToken) : null,
            new ApiStatusCounts(healths.Count(health => health == PortalHealth.Protected), healths.Count(health => health == PortalHealth.MonitoringOnly),
                healths.Count(health => health == PortalHealth.NeedsAttention), healths.Count(health => health == PortalHealth.NoReportsYet))));
    }
}
```

Check the claim type's name (`UserAccessClaimsTransformation.PermissionClaimType`, as `Program.cs` uses) and the API key handler's permission claims, and adapt. In `ApiEndpoints.MapDotMarcApi`, add `SummaryEndpoints.Map(api);` after `TlsReportEndpoints.Map(api);`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~SummaryEndpointTests|FullyQualifiedName~DashboardSummary|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS after `node scripts/update-openapi.mjs`.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api src/DotMarc/Reporting/DashboardSummary.cs test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add the dashboard's headline figures to the API, worked out in the database"
```

---

### Task 5: Client reports

**Files:**
- Create: `src/DotMarc/Api/ClientReportEndpoints.cs`
- Modify: `src/DotMarc/Reporting/ClientReports/ClientReportRunner.cs` (`ManualSendResult.DeliveryId`), `src/DotMarc/Api/ApiModels.cs`, `src/DotMarc/Api/ApiTags.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/ClientReportEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `ClientReportService.GetScheduleAsync`, `ClientReportService.GetNextScheduledAsync`, `ReportSettingsService.GetAsync`/`ResolveZone`, `ClientReportRunner.RenderAsync`/`SendNowAsync`/`ZoneAsync`, `ReportPeriods`, `ReportRecipients.Normalise`, `IEmailSenderFactory`
- Produces: `ManualSendResult(bool Sent, string Message, int? DeliveryId = null)`; `ApiTags.ClientReports` = `"Client reports"`; `ApiReportSchedule`, `ApiReportPeriod`, `ApiReportDelivery`, `ApiSendReportRequest`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/ClientReportEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Email;
using DotMarc.Reporting.ClientReports;
using DotMarc.Tests.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ClientReportEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private readonly RecordingSender _sender = new();
    private ApiTestHost _host = null!;

    public ClientReportEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    // The demo host has no email; give it a sender that records what it's asked to send.
    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture,
        configureServices: services => services.AddSingleton<IEmailSenderFactory>(new FixedSenderFactory(_sender)));

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSenderFactory(IEmailSender? sender) : IEmailSenderFactory
    {
        public Task<IEmailSender?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(sender);
    }

    private async Task<int> SeedScheduledGroupAsync(string name, params string[] recipients)
    {
        var groupId = await _host.SeedGroupAsync(name);
        await using var context = _host.CreateContext();
        await ClientReportService.SetScheduleAsync(context, TestActors.Admin, groupId, ReportFrequency.Monthly, recipients);
        return groupId;
    }

    [Fact]
    public async Task TheSchedule_AndItsNextRun()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-schedule", "it@contoso.example");
        var unscheduled = await _host.SeedGroupAsync("api-reports-none");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var schedule = await client.GetFromJsonAsync<ApiReportSchedule>($"/api/v1/groups/{groupId}/report-schedule");
        var none = await client.GetFromJsonAsync<ApiReportSchedule>($"/api/v1/groups/{unscheduled}/report-schedule");

        Assert.Equal(("Monthly", ["it@contoso.example"]), (schedule!.Frequency, schedule.Recipients.ToArray()));
        Assert.Equal("Month", schedule.NextPeriod!.Kind);
        Assert.NotNull(schedule.NextDueUtc);
        Assert.Equal(("Off", 0), (none!.Frequency, none.Recipients.Count));
        Assert.Null(none.NextPeriod);
    }

    [Fact]
    public async Task ThePdf_ForAMonth_AndPeriodRulesRefused()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-pdf", "it@contoso.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var pdf = await client.GetAsync($"/api/v1/groups/{groupId}/report?period=month&of=2026-09-15");
        var both = await client.GetAsync($"/api/v1/groups/{groupId}/report?period=month&start=2026-09-01&end=2026-09-30");
        var half = await client.GetAsync($"/api/v1/groups/{groupId}/report?start=2026-09-01");

        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Contains("September 2026", pdf.Content.Headers.ContentDisposition?.FileNameStar ?? pdf.Content.Headers.ContentDisposition?.FileName);
        Assert.Equal("%PDF-"u8.ToArray(), (await pdf.Content.ReadAsByteArrayAsync())[..5]);
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, half.StatusCode);
    }

    [Fact]
    public async Task SendNow_SendsRecordsAndAudits_AndShowsInTheHistory()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-send", "it@contoso.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage], name: "reports key");
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month", of = "2026-09-15" });
        var history = await client.GetFromJsonAsync<ApiPage<ApiReportDelivery>>($"/api/v1/groups/{groupId}/report-deliveries?status=sent&kind=Manual");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var delivery = (await response.Content.ReadFromJsonAsync<ApiReportDelivery>())!;
        Assert.Equal(("Sent", "Manual", ["it@contoso.example"]), (delivery.Status, delivery.Kind, delivery.Recipients.ToArray()));
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (delivery.PeriodStart, delivery.PeriodEnd));
        Assert.Single(_sender.Sent);
        Assert.Equal([delivery.Id], history!.Items.Select(item => item.Id));
        await using var context = _host.CreateContext();
        Assert.Contains(await context.AuditEntries.Where(entry => entry.Action == AuditActions.ClientReportSent).ToListAsync(),
            entry => entry.ActorName.StartsWith("reports key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendNow_RefusesBadRecipients_AndNoRecipients_WithoutSending()
    {
        var groupId = await SeedScheduledGroupAsync("api-reports-bad", "it@contoso.example");
        var empty = await _host.SeedGroupAsync("api-reports-empty");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var bad = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month", recipients = new[] { "not an address" } });
        var none = await client.PostAsJsonAsync($"/api/v1/groups/{empty}/report/send", new { period = "month" });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("\"recipients\"", await bad.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Empty(_sender.Sent);
        await using var context = _host.CreateContext();
        Assert.Empty(await context.ClientReportDeliveries.Where(delivery => delivery.GroupId == groupId || delivery.GroupId == empty).ToListAsync());
    }

    [Fact]
    public async Task AGroupLimitedKey_GetsA404ForAnotherGroup_AndNothingIsSent()
    {
        var own = await SeedScheduledGroupAsync("api-reports-own", "own@contoso.example");
        var other = await SeedScheduledGroupAsync("api-reports-other", "other@fabrikam.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage], scopedGroupIds: [own]);
        using var client = _host.ClientFor(secret);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report-schedule")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report-deliveries")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/groups/{other}/report?period=month")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"/api/v1/groups/{other}/report/send", new { period = "month" })).StatusCode);
        Assert.Empty(_sender.Sent);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/groups/{own}/report-schedule")).StatusCode);
    }
}

[Collection("Postgres")]
public sealed class ClientReportEmailOffTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ClientReportEmailOffTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task SendNow_WithEmailOff_IsA409()
    {
        var groupId = await _host.SeedGroupAsync("api-reports-off");
        await using (var context = _host.CreateContext())
        {
            await ClientReportService.SetScheduleAsync(context, TestActors.Admin, groupId, ReportFrequency.Monthly, ["it@contoso.example"]);
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.ReportsManage]);
        using var client = _host.ClientFor(secret);

        var response = await client.PostAsJsonAsync($"/api/v1/groups/{groupId}/report/send", new { period = "month" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
```

Check `ApiTestHost.StartAsync`'s optional parameter names (`settings`, `configureServices`) and `CreateKeyAsync`'s `name` parameter (the key's audit actor name starts with it), and `EmailMessage`'s namespace; adapt the test if any differ.

In `OpenApiDocumentTests`: count `21` to `25`; add `["GET /api/v1/groups/{group}/report-schedule"] = [404], ["GET /api/v1/groups/{group}/report-deliveries"] = [400, 404], ["GET /api/v1/groups/{group}/report"] = [400, 404], ["POST /api/v1/groups/{group}/report/send"] = [400, 404, 409],`; add `"Client reports"` to `sections`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportEndpointTests|FullyQualifiedName~ClientReportEmailOffTests"`
Expected: build FAIL, `ApiReportSchedule` is not defined.

- [ ] **Step 3: The delivery id from send now**

In `ClientReportRunner.cs`, change the record to `public sealed record ManualSendResult(bool Sent, string Message, int? DeliveryId = null);`, and the two returns after `SaveChangesAsync` to `return new ManualSendResult(true, $"Sent the {period.Label} report to ...", delivery.Id);` and `return new ManualSendResult(false, $"Couldn't send the report: {delivery.Error}", delivery.Id);` (keeping their messages).

- [ ] **Step 4: Models, section and endpoints**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiReportPeriod(DateOnly Start, DateOnly End, string Kind);

public sealed record ApiReportSchedule(string Frequency, IReadOnlyList<string> Recipients, DateTimeOffset? StartedUtc, ApiReportPeriod? NextPeriod, DateTimeOffset? NextDueUtc);

public sealed record ApiReportDelivery(int Id, DateOnly PeriodStart, DateOnly PeriodEnd, string Kind, string Status, IReadOnlyList<string> Recipients,
    int Attempts, DateTimeOffset? FirstAttemptUtc, DateTimeOffset? LastAttemptUtc, DateTimeOffset? SentUtc, string? Error, string? RequestedBy)
{
    public static ApiReportDelivery From(DotMarc.Reporting.ClientReports.ClientReportDelivery delivery) => new(
        delivery.Id, delivery.PeriodStart, delivery.PeriodEnd, delivery.Kind.ToString(), delivery.Status.ToString(), delivery.Recipients,
        delivery.Attempts, delivery.FirstAttemptUtc, delivery.LastAttemptUtc, delivery.SentUtc, delivery.Error, delivery.RequestedBy);
}

public sealed record ApiSendReportRequest(string? Period, string? Of, string? Start, string? End, IReadOnlyList<string>? Recipients);
```

In `ApiTags`, add `public const string ClientReports = "Client reports";` and `(ClientReports, "Each group's report schedule and delivery history, report PDFs for any period, and sending a report now."),`. In `ApiEndpoints.MapDotMarcApi`, add `ClientReportEndpoints.Map(api);` after `SummaryEndpoints.Map(api);`.

`src/DotMarc/Api/ClientReportEndpoints.cs`:

```csharp
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

        if (reader.Problem is not null || chosen is null)
        {
            // ReadPeriod records a problem whenever it can't give a period, so the reader always has one here.
            return reader.Problem!;
        }

        if (await senderFactory.GetAsync(cancellationToken) is null)
        {
            return ApiProblems.Conflict("Email isn't set up. Set it up on Email & reports in dotMARC first.");
        }

        var result = await runner.SendNowAsync(AuditActor.FromPrincipal(user), groupId, chosen, recipients, cancellationToken);
        if (result.DeliveryId is not { } deliveryId)
        {
            return TypedResults.Problem(result.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        var delivery = await context.ClientReportDeliveries.AsNoTracking().SingleAsync(candidate => candidate.Id == deliveryId, cancellationToken);
        return TypedResults.Ok(ApiReportDelivery.From(delivery));
    }
}
```

Check `ClientReportService.GetNextScheduledAsync`'s exact parameter list, `ReportSettings`' property names (`TimeZoneId`, `SendHour`), `ReportRecipients.Normalise`'s return type, and `AuditActor.FromPrincipal`'s namespace; adapt.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportEndpointTests|FullyQualifiedName~ClientReportEmailOffTests|FullyQualifiedName~ClientReports|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS after `node scripts/update-openapi.mjs`, including the existing runner tests.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api src/DotMarc/Reporting/ClientReports/ClientReportRunner.cs test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add client report schedules, deliveries, PDFs and send now to the API"
```

---

### Task 6: Parse failures and alert tickets

**Files:**
- Create: `src/DotMarc/Api/ParseFailureEndpoints.cs`
- Modify: `src/DotMarc/Api/AlertEndpoints.cs`, `src/DotMarc/Api/ApiModels.cs` (`ApiAlert` gains `Tickets`), `src/DotMarc/Api/ApiDocument.cs` (the `ApiAlert` example), `src/DotMarc/Api/ApiTags.cs`, `src/DotMarc/Api/ApiEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/ParseFailureEndpointTests.cs`, `test/DotMarc.Tests/Api/AlertEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Produces: `ApiParseFailure`, `ApiAlertTicket`; `ApiAlert` with a trailing `IReadOnlyList<ApiAlertTicket> Tickets`; `ApiTags.ParseFailures` = `"Parse failures"`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/ParseFailureEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class ParseFailureEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public ParseFailureEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task ParseFailures_ComeNewestFirst_AndFilter()
    {
        await using (var context = _host.CreateContext())
        {
            context.ParseFailures.AddRange(
                new ParseFailure { GraphMessageId = "api-pf-1", Reason = "api-pf schema validation", AttemptCount = 5, LastAttemptedUtc = new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero) },
                new ParseFailure { GraphMessageId = "api-pf-2", Reason = "api-pf not a report", AttemptCount = 1, LastAttemptedUtc = new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero) });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiParseFailure>>("/api/v1/parse-failures?search=api-pf");
        var stubborn = await client.GetFromJsonAsync<ApiPage<ApiParseFailure>>("/api/v1/parse-failures?search=API-PF&minAttempts=3&lastAttemptBefore=2026-09-03");

        Assert.Equal(["api-pf-2", "api-pf-1"], all!.Items.Select(failure => failure.MessageId));
        Assert.Equal(["api-pf-1"], stubborn!.Items.Select(failure => failure.MessageId));
    }

    [Fact]
    public async Task AGroupLimitedKey_IsRefused()
    {
        var group = await _host.SeedGroupAsync("api-pf-group");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsEdit], scopedGroupIds: [group]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/parse-failures");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("limited to certain groups", await response.Content.ReadAsStringAsync());
    }
}
```

Append to `AlertEndpointTests`:

```csharp
    [Fact]
    public async Task Alerts_CarryTheirTickets_AndFilterByAnOpenTicket()
    {
        await _host.SeedDomainAsync("api-tickets.example", monitored: false);
        var ticketed = await SeedAlertAsync("api-tickets.example", AlertTypes.MissedReport);
        var bare = await SeedAlertAsync("api-tickets.example", AlertTypes.TlsrptFailure);
        await using (var context = _host.CreateContext())
        {
            context.AlertTickets.Add(new DotMarc.Psa.AlertTicket { AlertEventId = ticketed, Psa = DotMarc.Psa.PsaKind.HaloPsa, TicketId = "T-1001", IsOpen = true, CreatedUtc = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }

        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var all = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example");
        var withTicket = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example&hasOpenTicket=true");
        var without = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-tickets.example&hasOpenTicket=false");

        var ticket = Assert.Single(all!.Items.Single(alert => alert.Id == ticketed).Tickets);
        Assert.Equal(("HaloPsa", "T-1001", true), (ticket.Psa, ticket.TicketId, ticket.Open));
        Assert.Empty(all.Items.Single(alert => alert.Id == bare).Tickets);
        Assert.Equal([ticketed], withTicket!.Items.Select(alert => alert.Id));
        Assert.Equal([bare], without!.Items.Select(alert => alert.Id));
    }
```

(Check `PsaKind`'s namespace and member names with a search and adapt; the expected string is whatever `ToString()` gives for HaloPSA.)

In `OpenApiDocumentTests`: count `25` to `26`; add `["GET /api/v1/parse-failures"] = [400],`; add `"Parse failures"` to `sections`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ParseFailureEndpointTests|FullyQualifiedName~Alerts_CarryTheirTickets"`
Expected: build FAIL, `ApiParseFailure` is not defined.

- [ ] **Step 3: Models**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiParseFailure(int Id, string MessageId, string Reason, int Attempts, DateTimeOffset LastAttemptUtc);

public sealed record ApiAlertTicket(string Psa, string TicketId, bool Open, DateTimeOffset CreatedUtc, DateTimeOffset? LastCheckedUtc);
```

Change `ApiAlert` to end `..., DateTimeOffset? ResolvedUtc, bool Acknowledgeable, IReadOnlyList<ApiAlertTicket> Tickets);`, and add `, []` to the `ApiAlert` example in `ApiExamples` (`ApiDocument.cs`). Search the tests for `new ApiAlert(` and add `, []` to each.

- [ ] **Step 4: Alert tickets**

In `AlertEndpoints`: add `[FromQuery(Name = "hasOpenTicket")] public string? HasOpenTicket { get; set; }` to `AlertFilters`; chain `.DocumentsFilter("hasOpenTicket", "true for alerts with an open PSA ticket, false for those without.", type: JsonSchemaType.Boolean)`; in the handler read `var hasOpenTicket = reader.Bool("hasOpenTicket", filters.HasOpenTicket);` with the other filters, and after the acknowledgeable block:

```csharp
        if (hasOpenTicket is { } wantsOpenTicket)
        {
            query = query.Where(alert => context.AlertTickets.Any(ticket => ticket.AlertEventId == alert.Id && ticket.IsOpen) == wantsOpenTicket);
        }
```

After the page of alerts is loaded:

```csharp
        var alertIds = alerts.Select(alert => alert.Id).ToList();
        var tickets = (await context.AlertTickets.AsNoTracking().Where(ticket => alertIds.Contains(ticket.AlertEventId))
                .OrderBy(ticket => ticket.CreatedUtc).ToListAsync(cancellationToken))
            .ToLookup(ticket => ticket.AlertEventId);
```

and pass `tickets[alert.Id].Select(ticket => new ApiAlertTicket(ticket.Psa.ToString(), ticket.TicketId, ticket.IsOpen, ticket.CreatedUtc, ticket.LastCheckedUtc)).ToList()` as the new last argument of `new ApiAlert(...)`.

- [ ] **Step 5: Parse failures**

In `ApiTags`, add `public const string ParseFailures = "Parse failures";` and `(ParseFailures, "Report emails dotMARC couldn't read, and why."),`. In `ApiEndpoints.MapDotMarcApi`, add `ParseFailureEndpoints.Map(api);` after `ClientReportEndpoints.Map(api);`.

`src/DotMarc/Api/ParseFailureEndpoints.cs`:

```csharp
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
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ParseFailureEndpointTests|FullyQualifiedName~AlertEndpointTests|FullyQualifiedName~OpenApiDocumentTests"`
Expected: PASS after `node scripts/update-openapi.mjs`.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add parse failures and PSA tickets on alerts to the API"
```

---

### Task 7: Docs and the whole suite

**Files:**
- Modify: `website/docs/api.mdx`

- [ ] **Step 1: The "Reports and downloads" section**

Add before `## Keys limited to groups` in `website/docs/api.mdx`:

```mdx
## Reports and downloads

- **Sending sources:** `GET /api/v1/domains/{domain}/sources` lists every IP that sent as the domain, busiest first, with
  its pass rate and, once dotMARC has looked it up, who owns it. The report summary only shows the top few.
- **Individual reports:** `GET /api/v1/domains/{domain}/reports` and `.../tls-reports` list the DMARC and TLS reports
  received. Add `/{id}/raw` to one report's address for the original file, exactly as the receiver sent it.
- **The dashboard's figures:** `GET /api/v1/summary` gives the headline numbers over every domain the key can see.
- **Client reports:** `GET /api/v1/groups/{group}/report?period=month&of=2026-09-15` downloads a group's report as a
  PDF, and `POST /api/v1/groups/{group}/report/send` sends one now, as the Reports dialog does. For example:

  ```bash
  curl -H "Authorization: Bearer dmk_..." -o report.pdf \
    "https://dotmarc.contoso.example/api/v1/groups/3/report?period=quarter&of=2026-07-01"
  ```

- **Parse failures:** `GET /api/v1/parse-failures` lists report emails dotMARC couldn't read, for a key with the
  DomainsEdit permission that isn't limited to groups.
- **PSA tickets:** each alert lists its tickets, and `hasOpenTicket=false` finds alerts no ticket has been raised for.
```

Check: `grep -c "—" website/docs/api.mdx` returns 0.

- [ ] **Step 2: Build the docs**

Run (from `website`): `npx docusaurus clean-api-docs all && npx docusaurus gen-api-docs all && npx docusaurus build --out-dir <scratch>/site`
Expected: success, no broken links.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test dotMARC.sln`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add website/docs/api.mdx
git commit -m "Document the API's reports, downloads, parse failures and tickets"
```
