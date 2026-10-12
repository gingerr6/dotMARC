# API Filters and Audit Log API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Granular filters on the API's list endpoints and a new `GET /audit` endpoint, on one set of filter conventions.

**Architecture:** One `ApiFilterReader` parses every filter by the shared conventions (lists, case-insensitive matching, dates, numbers) and collects every problem into a single 400. Each list endpoint takes its filters as an `[AsParameters]` class, so handlers stay readable. A small `ApiFilterDocs` endpoint metadata plus an OpenAPI operation transformer puts each filter's description and allowed values into the OpenAPI document.

**Tech Stack:** .NET 10 minimal APIs, EF Core 10 with Npgsql (`EF.Functions.ILike`), Microsoft.OpenApi via `Microsoft.AspNetCore.OpenApi`, xUnit with Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-12-api-filters-and-audit-design.md`

## Global Constraints

- Every current call keeps working and returns the same results; filters only narrow.
- Multi-value filters accept `a,b`, `x=a&x=b`, or both; "any of" within a filter, "all of" across filters.
- Values are case-insensitive; a value outside the allowed set is a 400 naming the filter and listing the accepted values, in the form `Use A, B or C.`; blank list entries are ignored.
- Dates: `yyyy-MM-dd` (00:00 UTC) or a date and time ending in `Z` or a `+hh:mm`/`-hh:mm` offset. `...After` inclusive, `...Before` exclusive; an `...After` not earlier than its `...Before` is a 400.
- A domain named in a filter that the key can't see is a 404, as on the `/domains/{domain}` routes.
- Paging: 50 a page by default, at most 200 (`DomainReadEndpoints.MaximumPageSize`), with the existing messages.
- No em dashes in prose, comments or commit messages. Meaningful variable names, including in tests.
- Tests run against the demo-seeded host (`ApiTestHost`), so every test seeds uniquely named domains and filters by them, never assuming an empty database.
- Test command for a filter: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~<Name>"`; whole suite `dotnet test dotMARC.sln`. After any change to an endpoint's shape, regenerate the committed OpenAPI file with `node scripts/update-openapi.mjs`.

## Review Focus

1. A filter given only blank entries (`type=,`): it must act as if absent, not match nothing. (Test in Task 1.)
2. `kind=all` on the audit log combined with another kind (`kind=all,change`): all kinds, not a 400. (Test in Task 5.)
3. A Group-limited key filtering alerts or domains by a Group outside its scope together with one inside: only the inside Group counts. (Test in Task 2.)
4. `passRateBelow` on `/domains` with paging: `totalCount` counts only matching domains and page 2 continues from page 1. (Test in Task 3.)
5. An audit entry for a deleted domain recorded with an id that a new domain later reuses can't happen (ids aren't reused in Postgres), but an entry recorded by name only (`AuditTarget.DomainNamed`) must still match a limited key's domain of that name. (Test in Task 5.)

---

### Task 1: The filter reader and a shared LIKE pattern

**Files:**
- Create: `src/DotMarc/Api/ApiFilterReader.cs`
- Create: `src/DotMarc/Data/LikePattern.cs`
- Modify: `src/DotMarc/Audit/AuditQuery.cs` (use `LikePattern`)
- Test: `test/DotMarc.Tests/Api/ApiFilterReaderTests.cs`

**Interfaces:**
- Produces:
  - `ApiFilterReader` (sealed class, `DotMarc.Api`):
    - `ValidationProblem? Problem` (null when nothing failed)
    - `static IReadOnlyList<string> Values(string[]? raw)`
    - `IReadOnlyList<string> OneOf(string name, string[]? raw, IReadOnlyList<string> allowed)` (returns the allowed spelling)
    - `string? Single(string name, string? raw, IReadOnlyList<string> allowed)`
    - `IReadOnlyList<TEnum> Enum<TEnum>(string name, string[]? raw) where TEnum : struct, System.Enum`
    - `IReadOnlyList<int> Ids(string name, string[]? raw)`
    - `DateTimeOffset? Date(string name, string? raw)`
    - `void Range(string afterName, DateTimeOffset? after, string beforeName, DateTimeOffset? before)`
    - `double? Fraction(string name, double? value)`
    - `(int Page, int PageSize) Paging(int? page, int? pageSize, int defaultPageSize)`
  - `LikePattern.Escape` (`"\\"`) and `LikePattern.Contains(string? text) -> string?` in `DotMarc.Data`

- [ ] **Step 1: Write the failing tests**

```csharp
using DotMarc.Api;
using DotMarc.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace DotMarc.Tests.Api;

public sealed class ApiFilterReaderTests
{
    private static readonly IReadOnlyList<string> Colours = ["Red", "Green", "Blue"];

    private static IDictionary<string, string[]> ErrorsOf(ApiFilterReader reader) =>
        Assert.IsType<ValidationProblem>(reader.Problem).ProblemDetails.Errors;

    [Fact]
    public void Values_AcceptsCommasAndRepeats_AndIgnoresBlanks() =>
        Assert.Equal(["a", "b", "c"], ApiFilterReader.Values(["a,b", " ", "c,", ",a"]));

    [Fact]
    public void OneOf_MatchesCaseInsensitively_ReturningTheAllowedSpelling()
    {
        var reader = new ApiFilterReader();

        Assert.Equal(["Red", "Blue"], reader.OneOf("colour", ["red,BLUE"], Colours));
        Assert.Null(reader.Problem);
    }

    [Fact]
    public void OneOf_WithOnlyBlanks_IsAbsent()
    {
        var reader = new ApiFilterReader();

        Assert.Empty(reader.OneOf("colour", [",", " "], Colours));
        Assert.Null(reader.Problem);
    }

    [Fact]
    public void AnUnknownValue_NamesTheFilter_AndListsWhatItAccepts()
    {
        var reader = new ApiFilterReader();

        reader.OneOf("colour", ["Purple"], Colours);

        Assert.Equal(["Use Red, Green or Blue."], ErrorsOf(reader)["colour"]);
    }

    [Fact]
    public void Enum_ParsesEnumNames()
    {
        var reader = new ApiFilterReader();

        Assert.Equal([SpfCheckStatus.TooManyLookups, SpfCheckStatus.Ok], reader.Enum<SpfCheckStatus>("spf", ["toomanylookups,ok"]));
    }

    [Fact]
    public void Ids_RefusesANonNumber()
    {
        var reader = new ApiFilterReader();

        Assert.Equal([3, 5], reader.Ids("group", ["3,5"]));
        reader.Ids("tag", ["x"]);

        Assert.Equal(["Use numeric ids, such as 3 or 3,5."], ErrorsOf(reader)["tag"]);
    }

    [Theory]
    [InlineData("2026-10-01", "2026-10-01T00:00:00+00:00")]
    [InlineData("2026-10-01T09:30:00Z", "2026-10-01T09:30:00+00:00")]
    [InlineData("2026-10-01T10:30:00+01:00", "2026-10-01T09:30:00+00:00")]
    public void Date_AcceptsADateOrADateTimeWithAZone(string raw, string expected)
    {
        var reader = new ApiFilterReader();

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), reader.Date("raisedAfter", raw));
        Assert.Null(reader.Problem);
    }

    [Theory]
    [InlineData("2026-10-01T09:30:00")]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    public void Date_RefusesAnythingElse(string raw)
    {
        var reader = new ApiFilterReader();

        Assert.Null(reader.Date("raisedAfter", raw));
        Assert.StartsWith("Use a date such as 2026-10-01", ErrorsOf(reader)["raisedAfter"][0]);
    }

    [Fact]
    public void Range_RefusesAnAfterThatIsntEarlierThanTheBefore()
    {
        var reader = new ApiFilterReader();
        var moment = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        reader.Range("raisedAfter", moment, "raisedBefore", moment);

        Assert.Equal(["Must be earlier than raisedBefore."], ErrorsOf(reader)["raisedAfter"]);
    }

    [Fact]
    public void Fraction_MustBeBetweenZeroAndOne()
    {
        var reader = new ApiFilterReader();

        reader.Fraction("passRateBelow", 1.5);

        Assert.Equal(["Must be between 0 and 1."], ErrorsOf(reader)["passRateBelow"]);
    }

    [Fact]
    public void Paging_KeepsTheExistingRules()
    {
        var reader = new ApiFilterReader();

        Assert.Equal((1, 50), reader.Paging(null, null, 50));
        reader.Paging(0, 201, 50);

        Assert.Equal(["Must be 1 or more."], ErrorsOf(reader)["page"]);
        Assert.Equal(["Must be between 1 and 200."], ErrorsOf(reader)["pageSize"]);
    }

    [Fact]
    public void EveryProblem_IsReportedTogether()
    {
        var reader = new ApiFilterReader();

        reader.OneOf("colour", ["Purple"], Colours);
        reader.Ids("group", ["x"]);

        Assert.Equal(["colour", "group"], ErrorsOf(reader).Keys.Order());
    }

    [Fact]
    public void LikePattern_EscapesWildcards() =>
        Assert.Equal("%100\\%\\_off%", LikePattern.Contains(" 100%_off "));
}
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiFilterReaderTests"`
Expected: build FAIL, `ApiFilterReader` and `LikePattern` are not defined.

- [ ] **Step 3: Implement**

`src/DotMarc/Data/LikePattern.cs`:

```csharp
namespace DotMarc.Data;

/// <summary>Case-insensitive "contains" searches with ILIKE, with LIKE's own wildcards escaped so "100%" means the text
/// "100%", not "100 then anything".</summary>
public static class LikePattern
{
    public const string Escape = "\\";

    public static string? Contains(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? null
            : "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
```

In `AuditQuery.cs`, delete `LikeEscape` and `ContainsPattern`, and use `LikePattern.Contains(...)` and `LikePattern.Escape` in their place (add `using DotMarc.Data;`).

`src/DotMarc/Api/ApiFilterReader.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotMarc.Api;

/// <summary>Reads the API's query filters by its shared conventions: several values as a list or repeated, any letter
/// case, dates as a day or a moment with a time zone. Every problem is collected so one 400 names them all.</summary>
public sealed partial class ApiFilterReader
{
    private readonly Dictionary<string, string[]> _errors = new(StringComparer.Ordinal);

    public ValidationProblem? Problem => _errors.Count == 0 ? null : TypedResults.ValidationProblem(_errors);

    /// <summary>A filter's values, from repeats and commas alike, trimmed, with blanks and repeats dropped.</summary>
    public static IReadOnlyList<string> Values(string[]? raw) =>
        (raw ?? [])
            .SelectMany(value => value.Split(','))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The values, each matched to its spelling in <paramref name="allowed"/>; any unknown value fails the filter.</summary>
    public IReadOnlyList<string> OneOf(string name, string[]? raw, IReadOnlyList<string> allowed)
    {
        var chosen = new List<string>();
        foreach (var value in Values(raw))
        {
            var match = allowed.FirstOrDefault(candidate => string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                Fail(name, $"Use {Readable(allowed)}.");
                return [];
            }

            chosen.Add(match);
        }

        return chosen;
    }

    /// <summary>A filter that takes one value.</summary>
    public string? Single(string name, string? raw, IReadOnlyList<string> allowed)
    {
        var chosen = OneOf(name, raw is null ? null : [raw], allowed);
        if (chosen.Count > 1)
        {
            Fail(name, "Give one value.");
            return null;
        }

        return chosen.Count == 1 ? chosen[0] : null;
    }

    public IReadOnlyList<TEnum> Enum<TEnum>(string name, string[]? raw) where TEnum : struct, System.Enum =>
        OneOf(name, raw, System.Enum.GetNames<TEnum>()).Select(System.Enum.Parse<TEnum>).ToList();

    public IReadOnlyList<int> Ids(string name, string[]? raw)
    {
        var ids = new List<int>();
        foreach (var value in Values(raw))
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                Fail(name, "Use numeric ids, such as 3 or 3,5.");
                return [];
            }

            ids.Add(id);
        }

        return ids;
    }

    /// <summary>A day (00:00 UTC) or a moment with its time zone. A time with no zone is refused rather than read in the
    /// server's own zone.</summary>
    public DateTimeOffset? Date(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }

        if (ZonePattern().IsMatch(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment))
        {
            return moment.ToUniversalTime();
        }

        Fail(name, "Use a date such as 2026-10-01, or a date and time with a time zone such as 2026-10-01T09:30:00Z.");
        return null;
    }

    public void Range(string afterName, DateTimeOffset? after, string beforeName, DateTimeOffset? before)
    {
        if (after is { } start && before is { } end && start >= end)
        {
            Fail(afterName, $"Must be earlier than {beforeName}.");
        }
    }

    public double? Fraction(string name, double? value)
    {
        if (value is < 0 or > 1)
        {
            Fail(name, "Must be between 0 and 1.");
            return null;
        }

        return value;
    }

    public (int Page, int PageSize) Paging(int? page, int? pageSize, int defaultPageSize)
    {
        var number = page ?? 1;
        var size = pageSize ?? defaultPageSize;
        if (number < 1)
        {
            Fail("page", "Must be 1 or more.");
        }

        if (size is < 1 or > DomainReadEndpoints.MaximumPageSize)
        {
            Fail("pageSize", $"Must be between 1 and {DomainReadEndpoints.MaximumPageSize}.");
        }

        return (number, size);
    }

    private void Fail(string name, string message) => _errors.TryAdd(name, [message]);

    private static string Readable(IReadOnlyList<string> values) =>
        values.Count == 1 ? values[0] : $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}";

    [GeneratedRegex(@"T.*(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZonePattern();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ApiFilterReaderTests|FullyQualifiedName~Audit"`
Expected: PASS, including the existing audit tests (the page still filters the same way).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Api/ApiFilterReader.cs src/DotMarc/Data/LikePattern.cs src/DotMarc/Audit/AuditQuery.cs test/DotMarc.Tests/Api/ApiFilterReaderTests.cs
git commit -m "Read API filters by one set of conventions, and share the escaped LIKE pattern"
```

---

### Task 2: Alert filters, and documenting filters in OpenAPI

**Files:**
- Create: `src/DotMarc/Api/ApiFilterDocs.cs`
- Modify: `src/DotMarc/Api/ApiDocument.cs` (operation transformer), `src/DotMarc/Api/AlertEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/AlertEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `ApiFilterReader` (Task 1), `ApiDomainKey.Matching` (existing)
- Produces:
  - `ApiFilterDocs(string Name, string Description, IReadOnlyList<string>? Values)` record and `RouteHandlerBuilder.DocumentsFilter(string name, string description, IEnumerable<string>? values = null)`
  - `ApiFilterDocs.ListHint` const: `" Repeat it or separate values with commas."`
  - `AlertEndpoints.Severities` (`IReadOnlyList<string>`: `Info`, `Warning`)

- [ ] **Step 1: Write the failing tests**

In `AlertEndpointTests`, give `SeedAlertAsync` a `string severity = "Warning"` parameter (used for `Severity`), and append:

```csharp
    [Fact]
    public async Task Filters_NarrowByTypeSeverityAndDomain_InAnyLetterCase()
    {
        var domainId = await _host.SeedDomainAsync("api-filter-alerts.example", monitored: false);
        var wanted = await SeedAlertAsync("api-filter-alerts.example", AlertTypes.MissedReport, severity: "Warning");
        await SeedAlertAsync("api-filter-alerts.example", AlertTypes.MissedReport, severity: "Info");
        await SeedAlertAsync("api-filter-alerts.example", AlertTypes.TlsrptFailure, severity: "Warning");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var byName = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-alerts.example&type=missedreport&severity=WARNING");
        var byId = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?domain={domainId}&type=MissedReport,TlsrptFailure&severity=Warning");

        Assert.Equal([wanted], byName!.Items.Select(alert => alert.Id));
        Assert.Equal(2, byId!.TotalCount);
    }

    [Fact]
    public async Task Status_Resolved_And_ResolvedDates_FindClosedAlerts()
    {
        await _host.SeedDomainAsync("api-filter-resolved.example", monitored: false);
        var open = await SeedAlertAsync("api-filter-resolved.example", AlertTypes.MissedReport);
        var closed = await SeedAlertAsync("api-filter-resolved.example", AlertTypes.MxRecordBroken, resolved: true);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);
        var since = DateTimeOffset.UtcNow.AddHours(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var resolved = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?domain=api-filter-resolved.example&status=resolved&resolvedAfter={since}");

        Assert.Equal([closed], resolved!.Items.Select(alert => alert.Id));
        Assert.DoesNotContain(open, resolved.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task GroupFilter_IgnoresAGroupOutsideAScopedKeysGroups()
    {
        var ownGroup = await _host.SeedGroupAsync("api-filter-alerts-own");
        var otherGroup = await _host.SeedGroupAsync("api-filter-alerts-other");
        await _host.SeedDomainAsync("api-filter-own.example", groupIds: [ownGroup], monitored: false);
        await _host.SeedDomainAsync("api-filter-other.example", groupIds: [otherGroup, ownGroup], monitored: false);
        var ownAlert = await SeedAlertAsync("api-filter-own.example", AlertTypes.MissedReport);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView], scopedGroupIds: [ownGroup]);
        using var client = _host.ClientFor(secret);

        var outsideOnly = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?group={otherGroup}");
        var both = await client.GetFromJsonAsync<ApiPage<ApiAlert>>($"/api/v1/alerts?group={otherGroup},{ownGroup}");

        Assert.Empty(outsideOnly!.Items);
        Assert.Contains(ownAlert, both!.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task Acknowledgeable_And_RaisedDates_Filter()
    {
        await _host.SeedDomainAsync("api-filter-ack.example", monitored: false);
        var weakened = await SeedAlertAsync("api-filter-ack.example", AlertTypes.DmarcPolicyWeakened);
        var old = await SeedAlertAsync("api-filter-ack.example", AlertTypes.MissedReport, createdUtc: new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero));
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var acknowledgeable = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-ack.example&acknowledgeable=true");
        var january = await client.GetFromJsonAsync<ApiPage<ApiAlert>>("/api/v1/alerts?domain=api-filter-ack.example&raisedAfter=2026-01-01&raisedBefore=2026-02-01");

        Assert.Equal([weakened], acknowledgeable!.Items.Select(alert => alert.Id));
        Assert.Equal([old], january!.Items.Select(alert => alert.Id));
    }

    [Fact]
    public async Task BadFilters_AreA400NamingThem_AndAnUnseenDomainIsA404()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AlertsView]);
        using var client = _host.ClientFor(secret);

        var badType = await client.GetAsync("/api/v1/alerts?type=NotAType&raisedAfter=2026-02-01&raisedBefore=2026-01-01");
        var unseen = await client.GetAsync("/api/v1/alerts?domain=nowhere-at-all.example");

        Assert.Equal(HttpStatusCode.BadRequest, badType.StatusCode);
        var body = await badType.Content.ReadAsStringAsync();
        Assert.Contains("\"type\"", body);
        Assert.Contains("MissedReport", body);
        Assert.Contains("Must be earlier than raisedBefore.", body);
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }
```

In `OpenApiDocumentTests`, change the alerts entry in `EveryOperation_DescribesEachErrorItCanReturn_AsAProblem` to `["GET /api/v1/alerts"] = [400, 404]`, and append:

```csharp
    [Fact]
    public async Task Filters_AreDescribed_WithTheirAllowedValues()
    {
        using var document = JsonDocument.Parse(await FetchDocumentAsync());
        var parameters = document.RootElement.GetProperty("paths").GetProperty("/api/v1/alerts").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().ToDictionary(parameter => parameter.GetProperty("name").GetString()!);

        var type = parameters["type"];
        Assert.Contains("commas", type.GetProperty("description").GetString());
        Assert.Equal("array", type.GetProperty("schema").GetProperty("type").GetString());
        Assert.Contains(type.GetProperty("schema").GetProperty("items").GetProperty("enum").EnumerateArray(), value => value.GetString() == "SpfRecordBroken");
        Assert.Equal(["open", "resolved", "all"], parameters["status"].GetProperty("schema").GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertEndpointTests|FullyQualifiedName~OpenApiDocumentTests.Filters_AreDescribed"`
Expected: the new tests FAIL (filters ignored, no enum in the document).

- [ ] **Step 3: The docs metadata and transformer**

`src/DotMarc/Api/ApiFilterDocs.cs`:

```csharp
namespace DotMarc.Api;

/// <summary>A query filter's description and, for a fixed set, its allowed values, for the OpenAPI document.</summary>
public sealed record ApiFilterDocs(string Name, string Description, IReadOnlyList<string>? Values)
{
    public const string ListHint = " Repeat it or separate values with commas.";
}

public static class ApiFilterDocsExtensions
{
    public static RouteHandlerBuilder DocumentsFilter(this RouteHandlerBuilder builder, string name, string description, IEnumerable<string>? values = null) =>
        builder.WithMetadata(new ApiFilterDocs(name, description, values?.ToList()));
}
```

In `ApiDocument`'s operation transformer, before `return Task.CompletedTask;`:

```csharp
                // Each filter's description and allowed values; a list filter's values describe its items.
                foreach (var docs in context.Description.ActionDescriptor.EndpointMetadata.OfType<ApiFilterDocs>())
                {
                    if (operation.Parameters?.FirstOrDefault(parameter => parameter.Name == docs.Name) is not OpenApiParameter parameter)
                    {
                        continue;
                    }

                    parameter.Description = docs.Description;
                    if (docs.Values is { } values && parameter.Schema is OpenApiSchema schema)
                    {
                        var valuesSchema = schema.Items as OpenApiSchema ?? schema;
                        valuesSchema.Enum = values.Select(value => (JsonNode)JsonValue.Create(value)!).ToList();
                    }
                }
```

If Microsoft.OpenApi's types differ (for example `Enum` being read-only or `Parameters` holding references), rule on the smallest change that produces the same JSON and ledger it; the test in Step 1 is the contract.

- [ ] **Step 4: The alert filters**

In `AlertEndpoints.cs`, add `using Microsoft.AspNetCore.Mvc;` and:

```csharp
    public static IReadOnlyList<string> Severities { get; } = ["Info", "Warning"];

    private static readonly IReadOnlyList<string> Statuses = ["open", "resolved", "all"];

    /// <summary>The alert list's filters; see the spec's section 3.</summary>
    public sealed class AlertFilters
    {
        [FromQuery(Name = "status")] public string? Status { get; set; }
        [FromQuery(Name = "type")] public string[]? Type { get; set; }
        [FromQuery(Name = "severity")] public string[]? Severity { get; set; }
        [FromQuery(Name = "domain")] public string[]? Domain { get; set; }
        [FromQuery(Name = "group")] public string[]? Group { get; set; }
        [FromQuery(Name = "tag")] public string[]? Tag { get; set; }
        [FromQuery(Name = "acknowledgeable")] public bool? Acknowledgeable { get; set; }
        [FromQuery(Name = "raisedAfter")] public string? RaisedAfter { get; set; }
        [FromQuery(Name = "raisedBefore")] public string? RaisedBefore { get; set; }
        [FromQuery(Name = "resolvedAfter")] public string? ResolvedAfter { get; set; }
        [FromQuery(Name = "resolvedBefore")] public string? ResolvedBefore { get; set; }
        [FromQuery(Name = "page")] public int? Page { get; set; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    }
```

Change the `/alerts` mapping's description to `$"Alerts newest first, open ones by default, {DefaultPageSize} a page by default and at most {DomainReadEndpoints.MaximumPageSize}. Filters combine: an alert must match every filter given. A key limited to certain groups sees only alerts about its domains."` and chain:

```csharp
            .ProducesProblem(StatusCodes.Status404NotFound)
            .DocumentsFilter("status", "open (default), resolved or all.", Statuses)
            .DocumentsFilter("type", "One or more alert types." + ApiFilterDocs.ListHint, AlertTypes.All.Select(type => type.Key))
            .DocumentsFilter("severity", "One or more severities." + ApiFilterDocs.ListHint, Severities)
            .DocumentsFilter("domain", "One or more domains, by id or name. A domain this key can't see is a 404." + ApiFilterDocs.ListHint)
            .DocumentsFilter("group", "One or more group ids: alerts for domains in any of them." + ApiFilterDocs.ListHint)
            .DocumentsFilter("tag", "One or more tag ids: alerts for domains with any of them." + ApiFilterDocs.ListHint)
            .DocumentsFilter("acknowledgeable", "true for open alerts this API can acknowledge, false for the rest.")
            .DocumentsFilter("raisedAfter", "Raised at or after this date (2026-10-01) or time with a zone (2026-10-01T09:30:00Z).")
            .DocumentsFilter("raisedBefore", "Raised before this date or time.")
            .DocumentsFilter("resolvedAfter", "Resolved at or after this date or time. Open alerts never match.")
            .DocumentsFilter("resolvedBefore", "Resolved before this date or time. Open alerts never match.")
```

Replace `ListAlertsAsync` with:

```csharp
    private static async Task<Results<Ok<ApiPage<ApiAlert>>, ValidationProblem, ProblemHttpResult>> ListAlertsAsync(
        [AsParameters] AlertFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var status = reader.Single("status", filters.Status, Statuses) ?? "open";
        var types = reader.OneOf("type", filters.Type, AlertTypes.All.Select(type => type.Key).ToList());
        var severities = reader.OneOf("severity", filters.Severity, Severities);
        var groupIds = reader.Ids("group", filters.Group);
        var tagIds = reader.Ids("tag", filters.Tag);
        var raisedAfter = reader.Date("raisedAfter", filters.RaisedAfter);
        var raisedBefore = reader.Date("raisedBefore", filters.RaisedBefore);
        var resolvedAfter = reader.Date("resolvedAfter", filters.ResolvedAfter);
        var resolvedBefore = reader.Date("resolvedBefore", filters.ResolvedBefore);
        reader.Range("raisedAfter", raisedAfter, "raisedBefore", raisedBefore);
        reader.Range("resolvedAfter", resolvedAfter, "resolvedBefore", resolvedBefore);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = context.AlertEvents.AsNoTracking();
        query = status switch
        {
            "open" => query.Where(alert => !alert.IsResolved),
            "resolved" => query.Where(alert => alert.IsResolved),
            _ => query,
        };

        var visibleDomains = scope.Domains(context.Domains.AsNoTracking());
        if (scope.IsScoped)
        {
            var visibleNames = visibleDomains.Select(domain => domain.Name);
            query = query.Where(alert => visibleNames.Contains(alert.DomainName));
        }

        if (types.Count > 0)
        {
            query = query.Where(alert => types.Contains(alert.AlertType));
        }

        if (severities.Count > 0)
        {
            query = query.Where(alert => severities.Contains(alert.Severity));
        }

        var domainNames = new List<string>();
        foreach (var domainKey in ApiFilterReader.Values(filters.Domain))
        {
            var name = await ApiDomainKey.Matching(visibleDomains, domainKey).Select(domain => domain.Name).SingleOrDefaultAsync(cancellationToken);
            if (name is null)
            {
                return ApiProblems.NotFound($"domain {domainKey}");
            }

            domainNames.Add(name);
        }

        if (domainNames.Count > 0)
        {
            query = query.Where(alert => domainNames.Contains(alert.DomainName));
        }

        if (groupIds.Count > 0)
        {
            // Groups outside the key's scope count for nothing, rather than revealing which visible domains are in them.
            var inScope = groupIds.Where(scope.Includes).ToList();
            var inGroups = visibleDomains.Where(domain => domain.Groups.Any(group => inScope.Contains(group.Id))).Select(domain => domain.Name);
            query = query.Where(alert => inGroups.Contains(alert.DomainName));
        }

        if (tagIds.Count > 0)
        {
            var tagged = visibleDomains.Where(domain => domain.Tags.Any(tag => tagIds.Contains(tag.Id))).Select(domain => domain.Name);
            query = query.Where(alert => tagged.Contains(alert.DomainName));
        }

        if (filters.Acknowledgeable is { } acknowledgeable)
        {
            var acknowledgeableTypes = AlertTypes.All.Select(type => type.Key).Where(AlertAcknowledgement.IsAcknowledgeable).ToList();
            query = acknowledgeable
                ? query.Where(alert => !alert.IsResolved && acknowledgeableTypes.Contains(alert.AlertType))
                : query.Where(alert => alert.IsResolved || !acknowledgeableTypes.Contains(alert.AlertType));
        }

        if (raisedAfter is { } raisedFrom)
        {
            query = query.Where(alert => alert.CreatedUtc >= raisedFrom);
        }

        if (raisedBefore is { } raisedUntil)
        {
            query = query.Where(alert => alert.CreatedUtc < raisedUntil);
        }

        if (resolvedAfter is { } resolvedFrom)
        {
            query = query.Where(alert => alert.ResolvedUtc != null && alert.ResolvedUtc >= resolvedFrom);
        }

        if (resolvedBefore is { } resolvedUntil)
        {
            query = query.Where(alert => alert.ResolvedUtc != null && alert.ResolvedUtc < resolvedUntil);
        }
```

followed by the existing body from `var totalCount = await query.CountAsync(cancellationToken);` to the end (the `domainIds` lookup uses `scope.Domains(context.Domains.AsNoTracking())` as before).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AlertEndpointTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~ApiFilterReaderTests"`
Expected: PASS except `TheCommittedCopy_MatchesTheApp`; then run `node scripts/update-openapi.mjs` and re-run: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Filter alerts by type, severity, domain, group, tag, acknowledgeability and dates, and document filters in OpenAPI"
```

---

### Task 3: Domain filters

**Files:**
- Modify: `src/DotMarc/Api/DomainReadEndpoints.cs`, `src/DotMarc/Portal/PortalData.cs:81` (`OpenAlertsAsync` from `private` to `internal`)
- Test: `test/DotMarc.Tests/Api/DomainReadEndpointTests.cs`

**Interfaces:**
- Consumes: `ApiFilterReader`, `ApiFilterDocs` (Tasks 1 and 2); `PortalStatus.For(Domain, bool, IReadOnlyList<AlertEvent>)`, `PortalData.OpenAlertsAsync(DotMarcDbContext, IReadOnlyList<string>, CancellationToken)`, `PassRatesAsync` (existing)
- Produces: `DomainReadEndpoints.DomainFilters` (`[AsParameters]` class)

- [ ] **Step 1: Write the failing tests** (append to `DomainReadEndpointTests`)

```csharp
    [Fact]
    public async Task HealthSearchAndPolicyFilters_Narrow()
    {
        var broken = await _host.SeedDomainAsync("api-dfilter-broken.example", configure: domain =>
        {
            domain.SpfCheckStatus = SpfCheckStatus.TooManyLookups;
            domain.DmarcPolicy = DmarcPolicyLevel.Reject;
        });
        await _host.SeedDomainAsync("api-dfilter-fine.example", configure: domain =>
        {
            domain.SpfCheckStatus = SpfCheckStatus.Ok;
            domain.DmarcPolicy = DmarcPolicyLevel.None;
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var bySpf = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&spf=toomanylookups,Misconfigured");
        var byPolicy = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=API-DFILTER-&policy=reject");

        Assert.Equal([broken], bySpf!.Items.Select(domain => domain.Id));
        Assert.Equal([broken], byPolicy!.Items.Select(domain => domain.Id));
    }

    [Fact]
    public async Task SeveralGroupsAndTags_MatchAnyOfThem()
    {
        var firstGroup = await _host.SeedGroupAsync("api-dfilter-group-1");
        var secondGroup = await _host.SeedGroupAsync("api-dfilter-group-2");
        var first = await _host.SeedDomainAsync("api-dfilter-g1.example", groupIds: [firstGroup]);
        var second = await _host.SeedDomainAsync("api-dfilter-g2.example", groupIds: [secondGroup]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>($"/api/v1/domains?group={firstGroup}&group={secondGroup}");

        Assert.Equal([first, second], page!.Items.Select(domain => domain.Id).Order());
    }

    [Fact]
    public async Task StatusFilter_MatchesThePortalsStatus()
    {
        var noReports = await _host.SeedDomainAsync("api-dfilter-new.example");
        await _host.SeedDomainAsync("api-dfilter-reported.example", configure: domain => domain.LastReportReceivedUtc = DateTimeOffset.UtcNow);
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&status=NoReportsYet");

        Assert.Contains(noReports, page!.Items.Select(domain => domain.Id));
        await using var context = _host.CreateContext();
        foreach (var item in page.Items)
        {
            var domain = await context.Domains.SingleAsync(candidate => candidate.Id == item.Id);
            Assert.Equal(DotMarc.Portal.PortalHealth.NoReportsYet, DotMarc.Portal.PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, []).Health);
        }
    }

    [Fact]
    public async Task PassRateFilters_PageCorrectly()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var index in Enumerable.Range(1, 3))
        {
            await _host.SeedDomainAsync($"api-dfilter-low{index}.example", configure: domain =>
                domain.Reports.Add(ReportWith(now.AddDays(-1), ("192.0.2.1", 10, AuthResult.Fail, AuthResult.Fail), ("192.0.2.2", 10, AuthResult.Pass, AuthResult.Fail))));
        }

        await _host.SeedDomainAsync("api-dfilter-high.example", configure: domain =>
            domain.Reports.Add(ReportWith(now.AddDays(-1), ("192.0.2.3", 10, AuthResult.Pass, AuthResult.Pass))));
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var firstPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&passRateBelow=0.9&pageSize=2");
        var secondPage = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&passRateBelow=0.9&pageSize=2&page=2");
        var high = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&passRateAtLeast=0.9");

        Assert.Equal(3, firstPage!.TotalCount);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Single(secondPage!.Items);
        Assert.Empty(firstPage.Items.Select(domain => domain.Id).Intersect(secondPage.Items.Select(domain => domain.Id)));
        Assert.Equal(["api-dfilter-high.example"], high!.Items.Select(domain => domain.Name));
    }

    [Fact]
    public async Task ReportDates_AndNoReports_Filter()
    {
        var stale = await _host.SeedDomainAsync("api-dfilter-stale.example", configure: domain => domain.LastReportReceivedUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var never = await _host.SeedDomainAsync("api-dfilter-never.example");
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var staleOnes = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&lastReportBefore=2026-02-01");
        var neverOnes = await client.GetFromJsonAsync<ApiPage<ApiDomain>>("/api/v1/domains?search=api-dfilter-&noReports=true");

        Assert.Contains(stale, staleOnes!.Items.Select(domain => domain.Id));
        Assert.DoesNotContain(never, staleOnes.Items.Select(domain => domain.Id));
        Assert.Contains(never, neverOnes!.Items.Select(domain => domain.Id));
        Assert.DoesNotContain(stale, neverOnes.Items.Select(domain => domain.Id));
    }

    [Fact]
    public async Task AnUnknownHealthValue_IsA400ListingTheStatuses()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/domains?spf=Broken");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("TooManyLookups", await response.Content.ReadAsStringAsync());
    }
```

(Add `using Microsoft.EntityFrameworkCore;` if the file lacks it.)

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpointTests"`
Expected: the new tests FAIL (filters ignored).

- [ ] **Step 3: Implement**

In `PortalData.cs`, change `private static async Task<ILookup<string, AlertEvent>> OpenAlertsAsync` to `internal static`.

In `DomainReadEndpoints.cs` (add `using Microsoft.AspNetCore.Mvc;` and `using DotMarc.Portal;`), add:

```csharp
    /// <summary>The domain list's filters; see the spec's section 3.</summary>
    public sealed class DomainFilters
    {
        [FromQuery(Name = "search")] public string? Search { get; set; }
        [FromQuery(Name = "group")] public string[]? Group { get; set; }
        [FromQuery(Name = "tag")] public string[]? Tag { get; set; }
        [FromQuery(Name = "monitored")] public bool? Monitored { get; set; }
        [FromQuery(Name = "dmarc")] public string[]? Dmarc { get; set; }
        [FromQuery(Name = "spf")] public string[]? Spf { get; set; }
        [FromQuery(Name = "dkim")] public string[]? Dkim { get; set; }
        [FromQuery(Name = "mx")] public string[]? Mx { get; set; }
        [FromQuery(Name = "tlsrpt")] public string[]? Tlsrpt { get; set; }
        [FromQuery(Name = "mtaSts")] public string[]? MtaSts { get; set; }
        [FromQuery(Name = "dmarcAuthorization")] public string[]? DmarcAuthorization { get; set; }
        [FromQuery(Name = "status")] public string[]? Status { get; set; }
        [FromQuery(Name = "policy")] public string[]? Policy { get; set; }
        [FromQuery(Name = "dnsProvider")] public string[]? DnsProvider { get; set; }
        [FromQuery(Name = "lastReportAfter")] public string? LastReportAfter { get; set; }
        [FromQuery(Name = "lastReportBefore")] public string? LastReportBefore { get; set; }
        [FromQuery(Name = "noReports")] public bool? NoReports { get; set; }
        [FromQuery(Name = "passRateBelow")] public double? PassRateBelow { get; set; }
        [FromQuery(Name = "passRateAtLeast")] public double? PassRateAtLeast { get; set; }
        [FromQuery(Name = "page")] public int? Page { get; set; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    }
```

On the `/domains` mapping, change the description to `$"Domains this key can see, in dashboard order, {DefaultPageSize} a page by default and at most {MaximumPageSize}. Filters combine: a domain must match every filter given. The pass rate covers the last 30 days and is null with no reports."` and chain:

```csharp
            .DocumentsFilter("search", "Part of the domain name, in any letter case.")
            .DocumentsFilter("group", "One or more group ids." + ApiFilterDocs.ListHint)
            .DocumentsFilter("tag", "One or more tag ids." + ApiFilterDocs.ListHint)
            .DocumentsFilter("dmarc", "One or more DMARC check results." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcCheckStatus>())
            .DocumentsFilter("spf", "One or more SPF check results." + ApiFilterDocs.ListHint, Enum.GetNames<SpfCheckStatus>())
            .DocumentsFilter("dkim", "One or more DKIM check results." + ApiFilterDocs.ListHint, Enum.GetNames<DkimCheckStatus>())
            .DocumentsFilter("mx", "One or more MX check results." + ApiFilterDocs.ListHint, Enum.GetNames<MxCheckStatus>())
            .DocumentsFilter("tlsrpt", "One or more TLS reporting check results." + ApiFilterDocs.ListHint, Enum.GetNames<TlsrptCheckStatus>())
            .DocumentsFilter("mtaSts", "One or more MTA-STS states." + ApiFilterDocs.ListHint, Enum.GetNames<DotMarc.MtaSts.MtaStsStatus>())
            .DocumentsFilter("dmarcAuthorization", "One or more DMARC authorization check results." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcAuthorizationCheckStatus>())
            .DocumentsFilter("status", "One or more plain-language statuses, worked out as the client portal does." + ApiFilterDocs.ListHint, Enum.GetNames<PortalHealth>())
            .DocumentsFilter("policy", "One or more published DMARC policies." + ApiFilterDocs.ListHint, Enum.GetNames<DmarcPolicyLevel>())
            .DocumentsFilter("dnsProvider", "One or more DNS providers." + ApiFilterDocs.ListHint, Enum.GetNames<DotMarc.Dns.DetectedDnsProvider>())
            .DocumentsFilter("lastReportAfter", "Last report received at or after this date or time. Domains with no report never match.")
            .DocumentsFilter("lastReportBefore", "Last report received before this date or time. Domains with no report never match.")
            .DocumentsFilter("noReports", "true for domains that have never had a report, false for those that have.")
            .DocumentsFilter("passRateBelow", "30-day pass rate below this, from 0 to 1. Domains with no pass rate never match.")
            .DocumentsFilter("passRateAtLeast", "30-day pass rate at least this, from 0 to 1. Domains with no pass rate never match.")
```

(Check the namespaces of `MtaStsStatus` and `DetectedDnsProvider` with a search and use the right ones.)

Replace `ListDomainsAsync` with:

```csharp
    private static async Task<Results<Ok<ApiPage<ApiDomain>>, ValidationProblem>> ListDomainsAsync(
        [AsParameters] DomainFilters filters, ClaimsPrincipal user, IDbContextFactory<DotMarcDbContext> dbFactory, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var reader = new ApiFilterReader();
        var groupIds = reader.Ids("group", filters.Group);
        var tagIds = reader.Ids("tag", filters.Tag);
        var dmarc = reader.Enum<DmarcCheckStatus>("dmarc", filters.Dmarc);
        var spf = reader.Enum<SpfCheckStatus>("spf", filters.Spf);
        var dkim = reader.Enum<DkimCheckStatus>("dkim", filters.Dkim);
        var mx = reader.Enum<MxCheckStatus>("mx", filters.Mx);
        var tlsrpt = reader.Enum<TlsrptCheckStatus>("tlsrpt", filters.Tlsrpt);
        var mtaSts = reader.Enum<DotMarc.MtaSts.MtaStsStatus>("mtaSts", filters.MtaSts);
        var dmarcAuthorization = reader.Enum<DmarcAuthorizationCheckStatus>("dmarcAuthorization", filters.DmarcAuthorization);
        var statuses = reader.Enum<PortalHealth>("status", filters.Status);
        var policies = reader.Enum<DmarcPolicyLevel>("policy", filters.Policy);
        var providers = reader.Enum<DotMarc.Dns.DetectedDnsProvider>("dnsProvider", filters.DnsProvider);
        var lastReportAfter = reader.Date("lastReportAfter", filters.LastReportAfter);
        var lastReportBefore = reader.Date("lastReportBefore", filters.LastReportBefore);
        reader.Range("lastReportAfter", lastReportAfter, "lastReportBefore", lastReportBefore);
        var passRateBelow = reader.Fraction("passRateBelow", filters.PassRateBelow);
        var passRateAtLeast = reader.Fraction("passRateAtLeast", filters.PassRateAtLeast);
        var (pageNumber, size) = reader.Paging(filters.Page, filters.PageSize, DefaultPageSize);
        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var scope = ApiScope.From(user);
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = scope.Domains(context.Domains.AsNoTracking());
        if (LikePattern.Contains(filters.Search) is { } searchPattern)
        {
            query = query.Where(domain => EF.Functions.ILike(domain.Name, searchPattern, LikePattern.Escape));
        }

        if (groupIds.Count > 0)
        {
            // Groups outside the key's scope count for nothing, rather than revealing which visible domains are in them.
            var inScope = groupIds.Where(scope.Includes).ToList();
            query = query.Where(domain => domain.Groups.Any(group => inScope.Contains(group.Id)));
        }

        if (tagIds.Count > 0)
        {
            query = query.Where(domain => domain.Tags.Any(tag => tagIds.Contains(tag.Id)));
        }

        if (filters.Monitored is { } isMonitored)
        {
            query = query.Where(domain => domain.IsMonitored == isMonitored);
        }

        if (dmarc.Count > 0) query = query.Where(domain => dmarc.Contains(domain.DmarcCheckStatus));
        if (spf.Count > 0) query = query.Where(domain => spf.Contains(domain.SpfCheckStatus));
        if (dkim.Count > 0) query = query.Where(domain => dkim.Contains(domain.DkimCheckStatus));
        if (mx.Count > 0) query = query.Where(domain => mx.Contains(domain.MxCheckStatus));
        if (tlsrpt.Count > 0) query = query.Where(domain => tlsrpt.Contains(domain.TlsrptCheckStatus));
        if (mtaSts.Count > 0) query = query.Where(domain => mtaSts.Contains(domain.MtaStsStatus));
        if (dmarcAuthorization.Count > 0) query = query.Where(domain => dmarcAuthorization.Contains(domain.DmarcAuthorizationCheckStatus));
        if (policies.Count > 0) query = query.Where(domain => domain.DmarcPolicy != null && policies.Contains(domain.DmarcPolicy.Value));
        if (providers.Count > 0) query = query.Where(domain => providers.Contains(domain.DnsProvider));
        if (lastReportAfter is { } reportFrom) query = query.Where(domain => domain.LastReportReceivedUtc != null && domain.LastReportReceivedUtc >= reportFrom);
        if (lastReportBefore is { } reportUntil) query = query.Where(domain => domain.LastReportReceivedUtc != null && domain.LastReportReceivedUtc < reportUntil);
        if (filters.NoReports is { } noReports) query = query.Where(domain => (domain.LastReportReceivedUtc == null) == noReports);

        var ordered = query.OrderBy(domain => domain.SortOrder).ThenBy(domain => domain.Name);
        var cutoffUtc = DomainStatistics.GetWindowCutoffUtc(timeProvider.GetUtcNow());
        int totalCount;
        List<int> pageIds;
        if (statuses.Count == 0 && passRateBelow is null && passRateAtLeast is null)
        {
            totalCount = await ordered.CountAsync(cancellationToken);
            pageIds = await ordered.Skip((pageNumber - 1) * size).Take(size).Select(domain => domain.Id).ToListAsync(cancellationToken);
        }
        else
        {
            // The status and pass rate are worked out outside the query, so filter every candidate here, then page.
            var candidates = await ordered.ToListAsync(cancellationToken);
            var candidateRates = await PassRatesAsync(context, candidates.Select(domain => domain.Id).ToList(), cutoffUtc, cancellationToken);
            var openAlerts = statuses.Count > 0
                ? await PortalData.OpenAlertsAsync(context, candidates.Select(domain => domain.Name).ToList(), cancellationToken)
                : null;
            var matching = candidates.Where(domain =>
            {
                var rate = PassRateOf(candidateRates, domain.Id);
                return (passRateBelow is null || (rate is { } below && below < passRateBelow))
                    && (passRateAtLeast is null || (rate is { } atLeast && atLeast >= passRateAtLeast))
                    && (statuses.Count == 0 || statuses.Contains(PortalStatus.For(domain, domain.LastReportReceivedUtc is not null, openAlerts![domain.Name].ToList()).Health));
            }).Select(domain => domain.Id).ToList();
            totalCount = matching.Count;
            pageIds = matching.Skip((pageNumber - 1) * size).Take(size).ToList();
        }

        var domains = (await context.Domains.AsNoTracking()
                .Where(domain => pageIds.Contains(domain.Id))
                .Include(domain => domain.Groups)
                .Include(domain => domain.Tags)
                .AsSplitQuery()
                .ToListAsync(cancellationToken))
            .OrderBy(domain => pageIds.IndexOf(domain.Id))
            .ToList();
        var passRates = await PassRatesAsync(context, pageIds, cutoffUtc, cancellationToken);
        var items = domains.Select(domain => ApiDomain.From(domain, scope, PassRateOf(passRates, domain.Id))).ToList();
        return TypedResults.Ok(new ApiPage<ApiDomain>(items, pageNumber, size, totalCount));
    }
```

`PassRateOf` already exists in this class; check its signature (`(Dictionary<int, double>, int) -> double?`) and adapt the calls if it differs. Add `using DotMarc.Data;` for `LikePattern` if needed. Existing tests for `group`, `tag` and `monitored` must keep passing unchanged.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpointTests|FullyQualifiedName~Portal"`
Expected: PASS.

- [ ] **Step 5: Regenerate the OpenAPI copy and commit**

Run: `node scripts/update-openapi.mjs`

```bash
git add src/DotMarc/Api/DomainReadEndpoints.cs src/DotMarc/Portal/PortalData.cs test/DotMarc.Tests/Api/DomainReadEndpointTests.cs website/data/openapi/dotmarc-api.json
git commit -m "Filter domains by name, health, status, policy, DNS provider, reports and pass rate"
```

---

### Task 4: Report summary ranges, and searching groups and tags

**Files:**
- Modify: `src/DotMarc/Api/DomainReadEndpoints.cs` (`GetReportSummaryAsync`), `src/DotMarc/Api/GroupAndTagEndpoints.cs`
- Test: `test/DotMarc.Tests/Api/DomainReadEndpointTests.cs`, `test/DotMarc.Tests/Api/GroupAndTagEndpointTests.cs`

**Interfaces:**
- Consumes: `ApiFilterReader`, `ApiFilterDocs`, `LikePattern`

- [ ] **Step 1: Write the failing tests**

Append to `DomainReadEndpointTests`:

```csharp
    [Fact]
    public async Task ReportSummary_TakesADateRange_AndHowManySources()
    {
        await _host.SeedDomainAsync("api-summary-range.example", configure: domain =>
        {
            domain.Reports.Add(ReportWith(new DateTimeOffset(2026, 3, 10, 0, 0, 0, TimeSpan.Zero), ("192.0.2.10", 5, AuthResult.Pass, AuthResult.Pass), ("192.0.2.11", 3, AuthResult.Pass, AuthResult.Pass)));
            domain.Reports.Add(ReportWith(new DateTimeOffset(2026, 4, 10, 0, 0, 0, TimeSpan.Zero), ("192.0.2.12", 7, AuthResult.Pass, AuthResult.Pass)));
        });
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        var march = await client.GetFromJsonAsync<ApiReportSummary>("/api/v1/domains/api-summary-range.example/reports/summary?from=2026-03-01&to=2026-04-01&top=1");
        var both = await client.GetAsync("/api/v1/domains/api-summary-range.example/reports/summary?days=7&from=2026-03-01");

        Assert.Equal((31, 8), (march!.Days, march.TotalVolume));
        Assert.Equal(["192.0.2.10"], march.TopSources.Select(source => source.SourceIp));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);
    }
```

Append to `GroupAndTagEndpointTests` (match the file's existing setup and key helpers):

```csharp
    [Fact]
    public async Task GroupsAndTags_CanBeSearchedByName()
    {
        await _host.SeedGroupAsync("api-search-Contoso");
        await _host.SeedTagAsync("api-search-Microsoft 365");
        var (_, secret) = await _host.CreateKeyAsync([Permission.GroupsView, Permission.TagsView]);
        using var client = _host.ClientFor(secret);

        var groups = await client.GetFromJsonAsync<List<ApiGroup>>("/api/v1/groups?search=SEARCH-cont");
        var tags = await client.GetFromJsonAsync<List<ApiTag>>("/api/v1/tags?search=microsoft");

        Assert.Equal(["api-search-Contoso"], groups!.Select(group => group.Name));
        Assert.Contains("api-search-Microsoft 365", tags!.Select(tag => tag.Name));
        Assert.All(tags, tag => Assert.Contains("microsoft", tag.Name, StringComparison.OrdinalIgnoreCase));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ReportSummary_TakesADateRange|FullyQualifiedName~GroupsAndTags_CanBeSearchedByName"`
Expected: FAIL (`from`, `to`, `top` and `search` ignored).

- [ ] **Step 3: Implement the report summary**

Change `GetReportSummaryAsync`'s parameters to `[FromRoute(Name = "domain")] string domainKey, int? days, string? from, string? to, int? top, ClaimsPrincipal user, ...` and replace its validation and cutoff with:

```csharp
        var reader = new ApiFilterReader();
        var maximumDays = (int)DomainStatistics.ReportWindow.TotalDays;
        var rangeStart = reader.Date("from", from);
        var rangeEnd = reader.Date("to", to);
        reader.Range("from", rangeStart, "to", rangeEnd);
        if (days is not null && (rangeStart is not null || rangeEnd is not null))
        {
            return ApiProblems.Validation("days", "Use days or from and to, not both.");
        }

        if (days is < 1 || days > maximumDays)
        {
            return ApiProblems.Validation("days", $"Must be between 1 and {maximumDays}.");
        }

        var sourceCount = top ?? TopSourceCount;
        if (sourceCount is < 1 or > 200)
        {
            return ApiProblems.Validation("top", "Must be between 1 and 200.");
        }

        if (reader.Problem is { } problem)
        {
            return problem;
        }

        var nowUtc = timeProvider.GetUtcNow();
        var endUtc = rangeEnd ?? nowUtc;
        var startUtc = rangeStart ?? endUtc.AddDays(-(days ?? maximumDays));
        if ((endUtc - startUtc).TotalDays > 366)
        {
            return ApiProblems.Validation("from", "The range can be at most 366 days.");
        }

        var windowDays = (int)Math.Ceiling((endUtc - startUtc).TotalDays);
```

In the query, replace each `report.ReceivedUtc >= cutoffUtc` with `report.ReceivedUtc >= startUtc && report.ReceivedUtc < endUtc`, and `.Take(TopSourceCount)` with `.Take(sourceCount)`. Chain on the mapping:

```csharp
            .DocumentsFilter("days", "The last 1 to 30 days (default 30). Use this or from and to.")
            .DocumentsFilter("from", "Start of the range: a date (2026-10-01) or time with a zone, included. At most 366 days before to.")
            .DocumentsFilter("to", "End of the range: a date or time, not included. Defaults to now.")
            .DocumentsFilter("top", "How many of the busiest sending IPs to return, 1 to 200 (default 20).")
```

and append to its description: `" Use days, or from and to for any range of up to 366 days."`

- [ ] **Step 4: Implement group and tag search**

Give both handlers a `string? search` parameter. In `ListGroupsAsync`, before `OrderBy`:

```csharp
        var groups = ApiScope.From(user).Groups(context.Groups.AsNoTracking());
        if (LikePattern.Contains(search) is { } pattern)
        {
            groups = groups.Where(group => EF.Functions.ILike(group.Name, pattern, LikePattern.Escape));
        }
```

then order, project and list as before. In `ListTagsAsync`, apply the same `Where` to `context.Tags.AsNoTracking()` before `OrderBy`. Chain `.DocumentsFilter("search", "Part of the name, in any letter case.")` on both mappings. Add `using DotMarc.Data;` where needed.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~DomainReadEndpointTests|FullyQualifiedName~GroupAndTagEndpointTests"`
Expected: PASS, including the existing `days` tests.

- [ ] **Step 6: Regenerate the OpenAPI copy and commit**

Run: `node scripts/update-openapi.mjs`

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Let the report summary take a date range and source count, and search groups and tags by name"
```

---

### Task 5: The audit log API

**Files:**
- Create: `src/DotMarc/Api/AuditEndpoints.cs`
- Modify: `src/DotMarc/Api/ApiEndpoints.cs` (map it), `src/DotMarc/Api/ApiTags.cs` (an "Audit log" section), `src/DotMarc/Api/ApiModels.cs` (models)
- Test: `test/DotMarc.Tests/Api/AuditEndpointTests.cs`, `test/DotMarc.Tests/Api/OpenApiDocumentTests.cs`

**Interfaces:**
- Consumes: `ApiFilterReader`, `ApiFilterDocs`, `LikePattern`, `ApiDomainKey`, `AuditActions.All` (`(string Action, string Label)` list)
- Produces: `ApiAuditEntry`, `ApiAuditActor`, `ApiAuditTarget`, `ApiAuditChange` records; `ApiTags.AuditLog`

- [ ] **Step 1: Write the failing tests**

`test/DotMarc.Tests/Api/AuditEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using DotMarc.Api;
using DotMarc.Audit;
using DotMarc.Data;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Api;

[Collection("Postgres")]
public sealed class AuditEndpointTests : IAsyncLifetime
{
    private readonly PostgresContainerFixture _fixture;
    private ApiTestHost _host = null!;

    public AuditEndpointTests(PostgresContainerFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() => _host = await ApiTestHost.StartAsync(_fixture);

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<long> SeedEntryAsync(string action, string? targetType = null, string? targetId = null, string? targetName = null,
        AuditEntryKind kind = AuditEntryKind.Change, string summary = "something happened", DateTimeOffset? occurredUtc = null,
        AuditActorKind actorKind = AuditActorKind.User, string actorName = "Alex Admin", List<AuditFieldChange>? changes = null)
    {
        await using var context = _host.CreateContext();
        var entry = new AuditEntry
        {
            OccurredUtc = occurredUtc ?? DateTimeOffset.UtcNow, Kind = kind, ActorKind = actorKind, ActorName = actorName,
            ActorEmail = actorKind == AuditActorKind.User ? "alex@contoso.example" : null, Action = action,
            TargetType = targetType, TargetId = targetId, TargetName = targetName, Summary = summary, Changes = changes ?? [],
        };
        context.AuditEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry.Id;
    }

    [Fact]
    public async Task TheAuditLog_NeedsAuditView()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.DomainsView]);
        using var client = _host.ClientFor(secret);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Entries_ComeNewestFirst_WithPageViewsLeftOutUnlessAskedFor()
    {
        var older = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", "990001", "api-audit-order.example", occurredUtc: DateTimeOffset.UtcNow.AddMinutes(-5), summary: "api-audit-order older");
        var newer = await SeedEntryAsync(AuditActions.DomainRemoved, "Domain", "990001", "api-audit-order.example", summary: "api-audit-order newer");
        var pageView = await SeedEntryAsync("page.viewed", kind: AuditEntryKind.PageView, summary: "api-audit-order page view");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-order");
        var withViews = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-order&kind=all,change");

        Assert.Equal([newer, older], page!.Items.Select(entry => entry.Id));
        Assert.Equal("change", page.Items[0].Kind);
        Assert.Contains(pageView, withViews!.Items.Select(entry => entry.Id));
    }

    [Fact]
    public async Task Filters_NarrowByActionActorTargetAndDates()
    {
        var wanted = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", "990002", "api-audit-filter.example",
            actorKind: AuditActorKind.ApiKey, actorName: "api-audit-sync key", occurredUtc: new DateTimeOffset(2026, 2, 10, 0, 0, 0, TimeSpan.Zero));
        await SeedEntryAsync(AuditActions.DomainRemoved, "Domain", "990002", "api-audit-filter.example",
            occurredUtc: new DateTimeOffset(2026, 2, 11, 0, 0, 0, TimeSpan.Zero));
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>(
            $"/api/v1/audit?action={AuditActions.DomainAdded}&actorKind=APIKEY&actor=audit-sync&targetType=domain&targetId=990002&occurredAfter=2026-02-01&occurredBefore=2026-03-01");

        var entry = Assert.Single(page!.Items);
        Assert.Equal(wanted, entry.Id);
        Assert.Equal(("apiKey", "api-audit-sync key"), (entry.Actor.Kind, entry.Actor.Name));
        Assert.Equal(new ApiAuditTarget("Domain", "990002", "api-audit-filter.example"), entry.Target);
    }

    [Fact]
    public async Task SecretChanges_ComeBackWithoutValues()
    {
        await SeedEntryAsync(AuditActions.DomainAdded, summary: "api-audit-secret", changes: [new AuditFieldChange("Client secret", null, null, Secret: true), new AuditFieldChange("Name", "a", "b")]);
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var entry = Assert.Single((await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-secret"))!.Items);

        Assert.Equal([new ApiAuditChange("Client secret", null, null, true), new ApiAuditChange("Name", "a", "b", false)], entry.Changes);
    }

    [Fact]
    public async Task AGroupLimitedKey_SeesOnlyItsDomainsGroupsAndAlerts()
    {
        var ownGroup = await _host.SeedGroupAsync("api-audit-own");
        var otherGroup = await _host.SeedGroupAsync("api-audit-other");
        var ownDomain = await _host.SeedDomainAsync("api-audit-own.example", groupIds: [ownGroup]);
        var otherDomain = await _host.SeedDomainAsync("api-audit-other.example", groupIds: [otherGroup]);
        var byId = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", ownDomain.ToString(System.Globalization.CultureInfo.InvariantCulture), "api-audit-own.example", summary: "api-audit-scope 1");
        var byName = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", null, "api-audit-own.example", summary: "api-audit-scope 2");
        var group = await SeedEntryAsync(AuditActions.DomainAdded, "Group", ownGroup.ToString(System.Globalization.CultureInfo.InvariantCulture), "api-audit-own", summary: "api-audit-scope 3");
        var alert = await SeedEntryAsync(AuditActions.DomainAdded, "Alert", "990003", "api-audit-own.example", summary: "api-audit-scope 4");
        var other = await SeedEntryAsync(AuditActions.DomainAdded, "Domain", otherDomain.ToString(System.Globalization.CultureInfo.InvariantCulture), "api-audit-other.example", summary: "api-audit-scope 5");
        var signIn = await SeedEntryAsync("user.signed_in", kind: AuditEntryKind.SignIn, summary: "api-audit-scope 6");
        var settings = await SeedEntryAsync(AuditActions.DomainAdded, "Settings", null, "Branding", summary: "api-audit-scope 7");
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView], scopedGroupIds: [ownGroup]);
        using var client = _host.ClientFor(secret);

        var page = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?search=api-audit-scope&pageSize=200");
        var forDomain = await client.GetFromJsonAsync<ApiPage<ApiAuditEntry>>("/api/v1/audit?domain=api-audit-own.example&pageSize=200");
        var unseen = await client.GetAsync("/api/v1/audit?domain=api-audit-other.example");

        Assert.Equal(new[] { byId, byName, group, alert }.Order(), page!.Items.Select(entry => entry.Id).Order());
        Assert.DoesNotContain(other, page.Items.Select(entry => entry.Id));
        Assert.DoesNotContain(signIn, page.Items.Select(entry => entry.Id));
        Assert.DoesNotContain(settings, page.Items.Select(entry => entry.Id));
        Assert.Equal(new[] { byId, byName, alert }.Order(), forDomain!.Items.Select(entry => entry.Id).Order());
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
    }

    [Fact]
    public async Task AnUnknownAction_IsA400()
    {
        var (_, secret) = await _host.CreateKeyAsync([Permission.AuditView]);
        using var client = _host.ClientFor(secret);

        var response = await client.GetAsync("/api/v1/audit?action=not.an.action");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(AuditActions.DomainAdded, await response.Content.ReadAsStringAsync());
    }
}
```

The seeded `"page.viewed"` and `"user.signed_in"` codes stand in for whatever `AuditActions` names them; use the real constants (search `AuditActions.cs` for the page view and sign-in codes).

In `OpenApiDocumentTests`: change `Assert.Equal(12, operations.Count)` to `13`; add `["GET /api/v1/audit"] = [400, 404]` to the error map; add `"Audit log"` to the `sections` array.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditEndpointTests|FullyQualifiedName~OpenApiDocumentTests"`
Expected: build FAIL (`ApiAuditEntry` not defined).

- [ ] **Step 3: Models and section**

Append to `ApiModels.cs`:

```csharp
public sealed record ApiAuditActor(string Kind, string Name, string? Email);

public sealed record ApiAuditTarget(string Type, string? Id, string? Name);

/// <summary>A changed field; a secret one has Secret true and no values, as stored.</summary>
public sealed record ApiAuditChange(string Field, string? Old, string? New, bool Secret);

public sealed record ApiAuditEntry(
    long Id, DateTimeOffset OccurredUtc, string Kind, ApiAuditActor Actor, string Action, ApiAuditTarget? Target, string Summary,
    IReadOnlyList<ApiAuditChange> Changes);
```

In `ApiTags`, add `public const string AuditLog = "Audit log";` and `(AuditLog, "Who did what in dotMARC: changes, sign-ins and, on request, pages opened.")` to `All`.

- [ ] **Step 4: The endpoint**

`src/DotMarc/Api/AuditEndpoints.cs`:

```csharp
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
                (entry.TargetType == "Domain" && (entry.TargetId != null ? domainIds.Contains(entry.TargetId) : domainNames.Contains(entry.TargetName!)))
                || (entry.TargetType == "Group" && entry.TargetId != null && groupIds.Contains(entry.TargetId))
                || (entry.TargetType == "Alert" && entry.TargetName != null && domainNames.Contains(entry.TargetName)));
        }

        if (kinds.Contains("all"))
        {
            // Every kind.
        }
        else if (kinds.Count > 0)
        {
            var chosen = kinds.Select(kind => Enum.Parse<AuditEntryKind>(kind, ignoreCase: true)).ToList();
            query = query.Where(entry => chosen.Contains(entry.Kind));
        }
        else
        {
            query = query.Where(entry => entry.Kind != AuditEntryKind.PageView);
        }

        if (actions.Count > 0) query = query.Where(entry => actions.Contains(entry.Action));
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

        if (targetTypes.Count > 0) query = query.Where(entry => entry.TargetType != null && targetTypes.Contains(entry.TargetType));
        if (targetIds.Count > 0) query = query.Where(entry => entry.TargetId != null && targetIds.Contains(entry.TargetId));

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
                (entry.TargetType == "Domain" && (entry.TargetId != null ? ids.Contains(entry.TargetId) : names.Contains(entry.TargetName!)))
                || (entry.TargetType == "Alert" && entry.TargetName != null && names.Contains(entry.TargetName)));
        }

        if (occurredAfter is { } from) query = query.Where(entry => entry.OccurredUtc >= from);
        if (occurredBefore is { } until) query = query.Where(entry => entry.OccurredUtc < until);
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
                entry.Changes.Select(change => new ApiAuditChange(change.Field, change.Old, change.New, change.Secret)).ToList()))
            .ToList();
        return TypedResults.Ok(new ApiPage<ApiAuditEntry>(items, pageNumber, size, totalCount));
    }
}
```

In `ApiEndpoints.MapDotMarcApi`, add `AuditEndpoints.Map(api);` after `AlertEndpoints.Map(api);`.

If EF can't translate the conditional inside the scoped `Where`, rewrite it as `(entry.TargetId != null && domainIds.Contains(entry.TargetId)) || (entry.TargetId == null && entry.TargetName != null && domainNames.Contains(entry.TargetName))`, which means the same.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~AuditEndpointTests|FullyQualifiedName~OpenApiDocumentTests|FullyQualifiedName~ApiAuthenticationTests"`
Expected: PASS except `TheCommittedCopy_MatchesTheApp`; then `node scripts/update-openapi.mjs` and re-run: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Api test/DotMarc.Tests/Api website/data/openapi/dotmarc-api.json
git commit -m "Add the audit log to the API, with filters, limited to a key's own domains and groups"
```

---

### Task 6: Docs and the whole suite

**Files:**
- Modify: `website/docs/api.mdx`

- [ ] **Step 1: The "Filtering" section**

Add after the "Finding a domain" section in `website/docs/api.mdx`:

```mdx
## Filtering

The list endpoints take filters as query parameters, so a script asks for just what it needs. Every filter is in the
[API reference](/api/dotmarc-api) with the values it accepts.

- **Several values:** give a filter more than one value with commas (`type=SpfRecordBroken,DkimRecordBroken`) or by
  repeating it (`type=SpfRecordBroken&type=DkimRecordBroken`). A record matches if it matches any of them.
- **Several filters:** a record must match every filter you give.
- **Letter case doesn't matter:** `spf=toomanylookups` works.
- **Domains** can be given by id or name. A domain the key can't see is a 404.
- **Dates** are a day (`2026-10-01`, from midnight UTC) or a time with its zone (`2026-10-01T09:30:00Z`). Filters ending
  `After` include the time you give; filters ending `Before` don't, so back-to-back ranges never overlap.
- **A value dotMARC doesn't recognise** is a 400 that names the filter and lists the values it accepts.

Some examples:

| To get | Ask for |
| --- | --- |
| Domains in group 3 whose SPF has too many lookups or is misconfigured | `GET /api/v1/domains?group=3&spf=TooManyLookups,Misconfigured` |
| Domains that need attention, as the client portal shows them | `GET /api/v1/domains?status=NeedsAttention` |
| Open SPF and DKIM alerts raised this week | `GET /api/v1/alerts?type=SpfRecordBroken,DkimRecordBroken&raisedAfter=2026-10-05` |
| Everything that happened to one domain | `GET /api/v1/audit?domain=contoso.example` |
| Sign-ins in September | `GET /api/v1/audit?kind=signIn&occurredAfter=2026-09-01&occurredBefore=2026-10-01` |

### The audit log

`GET /api/v1/audit` needs the AuditView permission. It returns changes and sign-ins by default; add `kind=pageView` or
`kind=all` for pages people opened. A key limited to certain groups sees only entries about its own domains, its groups
and their alerts, never sign-ins, settings or access changes.
```

Check for em dashes: `grep -c "—" website/docs/api.mdx` returns 0.

- [ ] **Step 2: Build the docs**

Run (from `website`): `npx docusaurus clean-api-docs all && npx docusaurus gen-api-docs all && npx docusaurus build --out-dir <scratch>/site`
Expected: success, no broken links.

- [ ] **Step 3: Run the whole suite**

Run: `dotnet test dotMARC.sln`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
git add website/docs/api.mdx
git commit -m "Document API filtering and the audit log"
```
