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
