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
