using DotMarc.Portal;
using MudBlazor.Utilities;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalThemeTests
{
    [Fact]
    public void TheBrandColours_AreThePortalsPrimaryAndSecondary_InLightAndDark()
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#0b5fffff", theme.PaletteLight.Primary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
        Assert.Equal("#ff6b00ff", theme.PaletteDark.Secondary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
    }

    [Theory]
    [InlineData("#7A1FA2")] // purple: faint on the dark surface as it is
    [InlineData("#0B1B3F")] // navy: nearly invisible on it
    public void InDarkMode_ADarkPrimary_IsLightenedUntilItReadsOnTheDarkSurface(string primary)
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", primary, "#FF6B00", null, null, null, null, null, null));

        var darkPrimary = theme.PaletteDark.Primary.ToString(MudColorOutputFormats.Hex);
        Assert.True(BrandColours.ContrastRatio(darkPrimary, PortalTheme.DarkSurface) >= BrandColours.MinimumTextContrast, $"{darkPrimary} on {PortalTheme.DarkSurface}");
        Assert.Equal(primary.ToLowerInvariant(), theme.PaletteLight.Primary.ToString(MudColorOutputFormats.Hex).ToLowerInvariant());
    }

    [Fact]
    public void InDarkMode_APrimaryThatAlreadyReads_IsKept()
    {
        var theme = PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#FFB74D", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#ffb74d", theme.PaletteDark.Primary.ToString(MudColorOutputFormats.Hex).ToLowerInvariant());
    }

    [Fact]
    public void TheDefaultTheme_IsLeftAlone()
    {
        PortalTheme.For(new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null));

        Assert.Equal("#e3594fff", PortalTheme.Default.PaletteLight.Primary.ToString(MudColorOutputFormats.HexA).ToLowerInvariant());
    }

    private static ResolvedBrand Brand() => new("Nova MSP", "Aurora Retail", "#0B5FFF", "#FF6B00", null, null, null, null, null, null);

    private static string HexOf(MudBlazor.Utilities.MudColor colour) => colour.ToString(MudColorOutputFormats.Hex).ToUpperInvariant();

    [Fact]
    public void DarkMode_UsesTheDarkBrandColours()
    {
        var theme = PortalTheme.For(Brand() with { ExplicitDarkPrimaryColour = "#8AB4F8", ExplicitDarkSecondaryColour = "#FFB74D" });

        Assert.Equal(("#8AB4F8", "#FFB74D"), (HexOf(theme.PaletteDark.Primary), HexOf(theme.PaletteDark.Secondary)));
    }

    [Fact]
    public void EachMode_UsesItsBackgrounds()
    {
        var theme = PortalTheme.For(Brand() with
        {
            LightBackgroundColour = "#F4F6FA", LightSurfaceColour = "#FFFFFF", DarkBackgroundColour = "#000000", DarkSurfaceColour = "#121212",
        });

        Assert.Equal(("#F4F6FA", "#FFFFFF"), (HexOf(theme.PaletteLight.Background), HexOf(theme.PaletteLight.Surface)));
        Assert.Equal(("#000000", "#121212"), (HexOf(theme.PaletteDark.Background), HexOf(theme.PaletteDark.Surface)));
    }

    [Fact]
    public void TheDarkAppBar_IsDotMarcsOwn_OnTheDefaultDarkPage() =>
        Assert.Equal("#0F1724", PortalTheme.DarkAppBar(Brand()));

    [Fact]
    public void TheDarkAppBar_IsJustDarkerThanACustomDarkPage()
    {
        var brand = Brand() with { DarkBackgroundColour = "#2A2A3A" };

        var appBar = PortalTheme.DarkAppBar(brand);

        Assert.True(BrandColours.ContrastRatio(appBar, "#FFFFFF") > BrandColours.ContrastRatio("#2A2A3A", "#FFFFFF"), appBar);
        Assert.Equal(appBar, HexOf(PortalTheme.For(brand).PaletteDark.AppbarBackground));
    }

    [Fact]
    public void ALightDarkModePage_GetsDarkAppBarText()
    {
        var theme = PortalTheme.For(Brand() with { DarkBackgroundColour = "#F0F0F0" });

        Assert.Equal("#000000", HexOf(theme.PaletteDark.AppbarText));
    }
}
