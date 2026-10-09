using System.Globalization;
using System.Text.RegularExpressions;
using MudBlazor.Utilities;

namespace DotMarc.Portal;

public static partial class BrandColours
{
    /// <summary>WCAG AA for normal text.</summary>
    public const double MinimumTextContrast = 4.5;

    public static bool IsValid(string? value) => value is not null && HexColour().IsMatch(value);

    /// <summary>MudBlazor's light page and card colours, which the portal used before backgrounds were configurable.</summary>
    public const string DefaultLightBackground = "#FFFFFF";
    public const string DefaultLightSurface = "#FFFFFF";

    /// <summary>dotMARC's own dark page and card colours.</summary>
    public const string DefaultDarkBackground = "#111827";
    public const string DefaultDarkSurface = "#1E2A3A";

    /// <summary>The normal text colour in each mode, which a background is rated against.</summary>
    public const string LightModeText = "#424242";
    public const string DarkModeText = "#E8EAF0";

    public static ContrastRating Rate(double ratio) => ratio switch
    {
        >= 7 => ContrastRating.AAA,
        >= MinimumTextContrast => ContrastRating.AA,
        >= 3 => ContrastRating.AALargeOnly,
        _ => ContrastRating.BelowAA,
    };

    /// <summary>The rating and ratio as the colour inputs show them, the ratio rounded down so one just under a threshold
    /// never reads as the threshold.</summary>
    public static string RatingText(double ratio)
    {
        var label = Rate(ratio) switch
        {
            ContrastRating.AAA => "AAA",
            ContrastRating.AA => "AA",
            ContrastRating.AALargeOnly => "AA large text only",
            _ => "Below AA",
        };
        var shown = Math.Floor(ratio * 10) / 10;
        return string.Create(CultureInfo.InvariantCulture, $"{label} {shown:0.0}:1");
    }

    /// <summary>A brand colour can sit on the page or on a card, so it's rated on whichever gives the lower ratio.</summary>
    public static double RatioOnBackgrounds(string colour, string background, string surface) =>
        Math.Min(ContrastRatio(colour, background), ContrastRatio(colour, surface));

    /// <summary>The colour dark mode uses when no dark colour is set: lightened in steps until it reads (AA) on the dark
    /// card colour. A colour that already reads is kept.</summary>
    public static string AutomaticDark(string colour, string darkSurface)
    {
        var candidate = new MudColor(colour);
        for (var step = 0; step < 20; step++)
        {
            if (ContrastRatio(Hex(candidate), darkSurface) >= MinimumTextContrast)
            {
                break;
            }

            candidate = candidate.ColorLighten(0.05);
        }

        return Hex(candidate);
    }

    private static string Hex(MudColor colour) => colour.ToString(MudColorOutputFormats.Hex).ToUpperInvariant();

    /// <summary>The WCAG contrast ratio between two #RRGGBB colours, from 1 (none) to 21 (black on white).</summary>
    public static double ContrastRatio(string first, string second)
    {
        var lighter = Math.Max(Luminance(first), Luminance(second));
        var darker = Math.Min(Luminance(first), Luminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double Luminance(string colour)
    {
        double Channel(int offset)
        {
            var value = int.Parse(colour.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.03928 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();
}

public enum ContrastRating { BelowAA, AALargeOnly, AA, AAA }
