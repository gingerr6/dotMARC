using System.Globalization;
using System.Text.RegularExpressions;
using DotMarc.Reporting;
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

        // An unencoded + in a query string arrives as a space, so "10:30:00 01:00" is the offset +01:00.
        var value = SpacedOffset().Replace(raw.Trim(), "+$1");
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

    /// <summary>A number from 0 to 1. Anything that isn't a finite number, NaN included, is refused rather than
    /// quietly matching nothing.</summary>
    public double? Fraction(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
        {
            Fail(name, "Use a number from 0 to 1, such as 0.95.");
            return null;
        }

        if (value is < 0 or > 1)
        {
            Fail(name, "Must be between 0 and 1.");
            return null;
        }

        return value;
    }

    public bool? Bool(string name, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (bool.TryParse(raw.Trim(), out var value))
        {
            return value;
        }

        Fail(name, "Use true or false.");
        return null;
    }

    public int? Integer(string name, string? raw, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value >= minimum && value <= maximum)
        {
            return value;
        }

        Fail(name, $"Use a whole number from {minimum} to {maximum}.");
        return null;
    }

    public (int Page, int PageSize) Paging(string? page, string? pageSize, int defaultPageSize)
    {
        var number = WholeNumber("page", page, "Use a whole number of 1 or more.") ?? 1;
        var size = WholeNumber("pageSize", pageSize, $"Use a whole number from 1 to {DomainReadEndpoints.MaximumPageSize}.") ?? defaultPageSize;
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

    /// <summary>Records a problem an endpoint finds itself, so it joins the others in the one 400.</summary>
    public void Refuse(string name, string message) => Fail(name, message);

    private int? WholeNumber(string name, string? raw, string message)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        Fail(name, message);
        return null;
    }

    private void Fail(string name, string message) => _errors.TryAdd(name, [message]);

    private static string Readable(IReadOnlyList<string> values) =>
        values.Count == 1 ? values[0] : $"{string.Join(", ", values.Take(values.Count - 1))} or {values[^1]}";

    [GeneratedRegex(@"T.*(Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex ZonePattern();

    [GeneratedRegex(@"(?<=T[\d:.]+) (\d{2}:\d{2})$")]
    private static partial Regex SpacedOffset();
}
