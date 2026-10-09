using DotMarc.Portal;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class BrandColoursTests
{
    [Theory]
    [InlineData("#1A2B3C", true)]
    [InlineData("#abcdef", true)]
    [InlineData("1A2B3C", false)]
    [InlineData("#FFF", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(null, false)]
    public void OnlySixDigitHexWithAHash_IsValid(string? value, bool expected) => Assert.Equal(expected, BrandColours.IsValid(value));

    [Fact]
    public void BlackOnWhite_Is21To1_AndAColourAgainstItself_Is1To1()
    {
        Assert.Equal(21, BrandColours.ContrastRatio("#000000", "#FFFFFF"), precision: 2);
        Assert.Equal(1, BrandColours.ContrastRatio("#3366CC", "#3366CC"), precision: 2);
    }

    [Theory]
    [InlineData(7.0, ContrastRating.AAA)]
    [InlineData(6.99, ContrastRating.AA)]
    [InlineData(4.5, ContrastRating.AA)]
    [InlineData(4.49, ContrastRating.AALargeOnly)]
    [InlineData(3.0, ContrastRating.AALargeOnly)]
    [InlineData(2.99, ContrastRating.BelowAA)]
    public void Rate_FollowsTheWcagThresholds(double ratio, ContrastRating expected) => Assert.Equal(expected, BrandColours.Rate(ratio));

    [Theory]
    [InlineData(8.24, "AAA 8.2:1")]
    [InlineData(5.1, "AA 5.1:1")]
    [InlineData(4.49, "AA large text only 4.4:1")]
    [InlineData(2.1, "Below AA 2.1:1")]
    public void RatingText_NamesTheRating_AndRoundsTheRatioDown(double ratio, string expected) => Assert.Equal(expected, BrandColours.RatingText(ratio));

    [Fact]
    public void RatioOnBackgrounds_IsTheLowerOfPageAndCard()
    {
        var onWhite = BrandColours.ContrastRatio("#0B5FFF", "#FFFFFF");
        var onGrey = BrandColours.ContrastRatio("#0B5FFF", "#DDDDDD");

        Assert.Equal(Math.Min(onWhite, onGrey), BrandColours.RatioOnBackgrounds("#0B5FFF", "#FFFFFF", "#DDDDDD"), precision: 6);
    }

    [Theory]
    [InlineData("#7A1FA2")]
    [InlineData("#0B1B3F")]
    public void AutomaticDark_LightensAColourUntilItReadsOnTheDarkSurface(string colour)
    {
        var automatic = BrandColours.AutomaticDark(colour, BrandColours.DefaultDarkSurface);

        Assert.True(BrandColours.IsValid(automatic), automatic);
        Assert.True(BrandColours.ContrastRatio(automatic, BrandColours.DefaultDarkSurface) >= BrandColours.MinimumTextContrast, automatic);
    }

    [Fact]
    public void AutomaticDark_KeepsAColourThatAlreadyReads() =>
        Assert.Equal("#FFB74D", BrandColours.AutomaticDark("#FFB74D", BrandColours.DefaultDarkSurface));

    [Fact]
    public void AutomaticDark_OnALightDarkModeCard_DarkensInsteadOfTurningWhite()
    {
        var automatic = BrandColours.AutomaticDark("#FF6B00", "#FFFFFF");

        Assert.NotEqual("#FFFFFF", automatic);
        Assert.True(BrandColours.ContrastRatio(automatic, "#FFFFFF") >= BrandColours.MinimumTextContrast, automatic);
    }

    [Fact]
    public void AutomaticDark_FollowsACustomDarkSurface()
    {
        var automatic = BrandColours.AutomaticDark("#0B5FFF", "#000000");

        Assert.True(BrandColours.ContrastRatio(automatic, "#000000") >= BrandColours.MinimumTextContrast, automatic);
    }
}
