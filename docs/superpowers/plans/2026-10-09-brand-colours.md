# Brand colours Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Light and dark brand colours (MSP and Group), MSP-wide page and card backgrounds per mode, a colour input with a swatch and WCAG rating, and the secondary colour used as an accent in the portal, PDF report and email.

**Architecture:** `BrandColours` gains the contrast rating, defaults and the automatic dark-colour rule. `ResolvedBrand` keeps its positional constructor and gains init properties for backgrounds and explicit dark colours, with computed `DarkPrimaryColour`/`DarkSecondaryColour`, so the eight existing constructions keep compiling. `PortalBranding.Resolve` fills them; `PortalTheme.For` maps them onto MudBlazor's palettes; the Razor components only read the resolved brand.

**Tech Stack:** .NET 10, Blazor Server, MudBlazor 9.8, EF Core 10 with Npgsql, xUnit with Testcontainers, PDFsharp-MigraDoc 6.2.4.

**Spec:** `docs/superpowers/specs/2026-10-09-brand-colours-design.md`

## Global Constraints

- No em dashes in any prose (UI text, docs, comments, commit messages).
- Meaningful variable names, including in tests; no `r`, `m`, `x` style names.
- Colours are stored and compared as `#RRGGBB`, upper case when saved (`BrandColours.IsValid`).
- Defaults: light page `#FFFFFF`, light card `#FFFFFF`, dark page `#111827`, dark card `#1E2A3A`, dark app bar for the default dark page `#0F1724`.
- Text colours used for rating: light `#424242`, dark `#E8EAF0`.
- Ratings: AAA at 7:1 and above, AA at 4.5:1, "AA large text only" at 3:1, "Below AA" under 3:1. A colour below AA can always be saved.
- Ratios are shown rounded down to one decimal, so a ratio just under a threshold never displays as the threshold.
- "Protected" stays `Color.Success` and "Needs attention" `Color.Warning`.
- Groups never get background fields.
- Browser checks use playwright-edge only; never install Chrome.
- Static service methods that change data take `(DotMarcDbContext, AuditActor, ...)` first (existing audit coverage rule).
- Test command for a filter: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~<Name>"`; whole suite: `dotnet test dotMARC.sln`.

## Review Focus

1. A dark page background set to a light colour: the dark app bar follows it, so the app bar text and the app bar logo must switch to suit a light bar, not stay white-on-light. (Test in Task 3.)
2. A Group that sets only its light primary while the MSP has an explicit dark primary: the Group's dark primary must be automatic from the Group's colour, never the MSP's dark one. (Test in Task 2.)
3. A Group that sets only dark colours and nothing else must still keep its branding row (the "cleared" check must count dark colours). (Test in Task 4.)
4. A ratio of 4.49:1 must rate "AA large text only" and display "4.4:1", not "4.5:1". (Test in Task 1.)
5. An unsaved invalid colour typed on the branding page must not break the preview; it falls back as today. (Browser check in Task 7, and `PreviewSettings` keeps the existing fallbacks.)

---

### Task 1: Contrast rating, defaults and the automatic dark colour

**Files:**
- Modify: `src/DotMarc/Portal/BrandColours.cs`
- Modify: `src/DotMarc/Portal/PortalTheme.cs` (use the moved helper)
- Test: `test/DotMarc.Tests/Portal/BrandColoursTests.cs`

**Interfaces:**
- Produces:
  - `BrandColours.DefaultLightBackground`, `DefaultLightSurface`, `DefaultDarkBackground`, `DefaultDarkSurface`, `LightModeText`, `DarkModeText` (string constants)
  - `enum ContrastRating { BelowAA, AALargeOnly, AA, AAA }` in `DotMarc.Portal`
  - `BrandColours.Rate(double ratio) -> ContrastRating`
  - `BrandColours.RatioOnBackgrounds(string colour, string background, string surface) -> double`
  - `BrandColours.AutomaticDark(string colour, string darkSurface) -> string` (upper-case `#RRGGBB`)
  - `BrandColours.RatingText(double ratio) -> string`, for example `"AA 5.1:1"`

- [ ] **Step 1: Write the failing tests** (append to `BrandColoursTests`)

```csharp
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
    public void AutomaticDark_FollowsACustomDarkSurface()
    {
        var automatic = BrandColours.AutomaticDark("#0B5FFF", "#000000");

        Assert.True(BrandColours.ContrastRatio(automatic, "#000000") >= BrandColours.MinimumTextContrast, automatic);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandColoursTests"`
Expected: build FAIL, `ContrastRating` and `BrandColours.Rate` are not defined.

- [ ] **Step 3: Implement in `BrandColours.cs`**

Add `using MudBlazor.Utilities;` and, inside the class:

```csharp
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
```

And after the class, in the same file:

```csharp
public enum ContrastRating { BelowAA, AALargeOnly, AA, AAA }
```

In `PortalTheme.cs`, change `DarkSurface` to `public const string DarkSurface = BrandColours.DefaultDarkSurface;`, delete `ReadableOnDarkSurface`, and replace its use with `new MudBlazor.Utilities.MudColor(BrandColours.AutomaticDark(brand.PrimaryColour, DarkSurface))` (Task 3 replaces this again).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandColoursTests|FullyQualifiedName~PortalThemeTests"`
Expected: PASS, including the existing theme tests.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Portal/BrandColours.cs src/DotMarc/Portal/PortalTheme.cs test/DotMarc.Tests/Portal/BrandColoursTests.cs
git commit -m "Rate brand colours against WCAG, and move the automatic dark colour into BrandColours"
```

---

### Task 2: Stored colours, migration and resolving a brand

**Files:**
- Modify: `src/DotMarc/Portal/BrandingSettings.cs`, `src/DotMarc/Portal/GroupBranding.cs`, `src/DotMarc/Portal/PortalBranding.cs`
- Modify: `src/DotMarc/Data/DotMarcDbContext.cs:422-485`
- Create: migration `AddBrandColourModes` (generated)
- Test: `test/DotMarc.Tests/Portal/PortalBrandingTests.cs`

**Interfaces:**
- Consumes: `BrandColours.Default*`, `BrandColours.AutomaticDark` (Task 1)
- Produces:
  - `BrandingSettings.DarkPrimaryColour`, `DarkSecondaryColour`, `LightBackgroundColour`, `LightSurfaceColour`, `DarkBackgroundColour`, `DarkSurfaceColour` (all `string?`)
  - `GroupBranding.DarkPrimaryColour`, `DarkSecondaryColour` (`string?`)
  - `GroupBrandingInput(string? DisplayName, Guid? LogoImageId, Guid? DarkLogoImageId, string? PrimaryColour, string? SecondaryColour, string? DarkPrimaryColour = null, string? DarkSecondaryColour = null)`
  - `ResolvedBrand` init properties `LightBackgroundColour`, `LightSurfaceColour`, `DarkBackgroundColour`, `DarkSurfaceColour` (string, defaulted), `ExplicitDarkPrimaryColour`, `ExplicitDarkSecondaryColour` (string?), and computed `DarkPrimaryColour`, `DarkSecondaryColour` (string)

- [ ] **Step 1: Write the failing tests** (append to `PortalBrandingTests`)

```csharp
    [Fact]
    public void WithNoDarkColours_DarkModeUsesTheAutomaticOnes()
    {
        var brand = PortalBranding.Resolve(Msp(), [new ScopedGroupBrand("Aurora Retail", null)]);

        Assert.Equal(BrandColours.AutomaticDark("#0B5FFF", BrandColours.DefaultDarkSurface), brand.DarkPrimaryColour);
        Assert.Equal(BrandColours.AutomaticDark("#FF6B00", BrandColours.DefaultDarkSurface), brand.DarkSecondaryColour);
    }

    [Fact]
    public void TheMspsDarkColours_AreUsed_WhenSet()
    {
        var msp = Msp();
        msp.DarkPrimaryColour = "#8AB4F8";
        msp.DarkSecondaryColour = "#FFB74D";

        var brand = PortalBranding.Resolve(msp, [new ScopedGroupBrand("Aurora Retail", null)]);

        Assert.Equal(("#8AB4F8", "#FFB74D"), (brand.DarkPrimaryColour, brand.DarkSecondaryColour));
    }

    [Fact]
    public void AGroupsOwnDarkColour_WinsOverTheMsps()
    {
        var msp = Msp();
        msp.DarkPrimaryColour = "#8AB4F8";

        var brand = PortalBranding.Resolve(msp, [new ScopedGroupBrand("Aurora Retail", new GroupBranding { DarkPrimaryColour = "#CE93D8" })]);

        Assert.Equal("#CE93D8", brand.DarkPrimaryColour);
    }

    [Fact]
    public void AGroupsLightColour_NeverPairsWithTheMspsDarkOne()
    {
        var msp = Msp();
        msp.DarkPrimaryColour = "#8AB4F8";

        var brand = PortalBranding.Resolve(msp, [new ScopedGroupBrand("Aurora Retail", new GroupBranding { PrimaryColour = "#7A1FA2" })]);

        Assert.Equal(BrandColours.AutomaticDark("#7A1FA2", BrandColours.DefaultDarkSurface), brand.DarkPrimaryColour);
    }

    [Fact]
    public void Backgrounds_ComeFromTheMsp_OrTheDefaults()
    {
        var msp = Msp();
        msp.DarkSurfaceColour = "#000000";

        var brand = PortalBranding.Resolve(msp, [new ScopedGroupBrand("Aurora Retail", null)]);

        Assert.Equal((BrandColours.DefaultLightBackground, BrandColours.DefaultLightSurface, BrandColours.DefaultDarkBackground, "#000000"),
            (brand.LightBackgroundColour, brand.LightSurfaceColour, brand.DarkBackgroundColour, brand.DarkSurfaceColour));
        Assert.Equal(BrandColours.AutomaticDark("#0B5FFF", "#000000"), brand.DarkPrimaryColour);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalBrandingTests"`
Expected: build FAIL, `BrandingSettings.DarkPrimaryColour` is not defined.

- [ ] **Step 3: Add the stored colours**

In `BrandingSettings`, after `SecondaryColour`:

```csharp
    /// <summary>Dark mode's brand colours. Null means automatic: the light colour, lightened until it reads on the dark
    /// card colour.</summary>
    public string? DarkPrimaryColour { get; set; }
    public string? DarkSecondaryColour { get; set; }

    /// <summary>The page and card backgrounds for each mode. Null means the default (see <see cref="BrandColours"/>).</summary>
    public string? LightBackgroundColour { get; set; }
    public string? LightSurfaceColour { get; set; }
    public string? DarkBackgroundColour { get; set; }
    public string? DarkSurfaceColour { get; set; }
```

In `GroupBranding`, after `SecondaryColour`:

```csharp
    /// <summary>Dark mode's colours for this client. Null means automatic from this Group's light colour, or the MSP's
    /// dark colour when this Group has no light colour of its own.</summary>
    public string? DarkPrimaryColour { get; set; }
    public string? DarkSecondaryColour { get; set; }
```

and replace the input record:

```csharp
public sealed record GroupBrandingInput(string? DisplayName, Guid? LogoImageId, Guid? DarkLogoImageId, string? PrimaryColour, string? SecondaryColour,
    string? DarkPrimaryColour = null, string? DarkSecondaryColour = null);
```

In `DotMarcDbContext`, add to the `BrandingSettings` entity:

```csharp
            entity.Property(settings => settings.DarkPrimaryColour).HasMaxLength(7);
            entity.Property(settings => settings.DarkSecondaryColour).HasMaxLength(7);
            entity.Property(settings => settings.LightBackgroundColour).HasMaxLength(7);
            entity.Property(settings => settings.LightSurfaceColour).HasMaxLength(7);
            entity.Property(settings => settings.DarkBackgroundColour).HasMaxLength(7);
            entity.Property(settings => settings.DarkSurfaceColour).HasMaxLength(7);
```

and to the `GroupBranding` entity:

```csharp
            entity.Property(branding => branding.DarkPrimaryColour).HasMaxLength(7);
            entity.Property(branding => branding.DarkSecondaryColour).HasMaxLength(7);
```

- [ ] **Step 4: Extend `ResolvedBrand` and `Resolve`**

Replace the `ResolvedBrand` declaration in `PortalBranding.cs`:

```csharp
/// <summary>The brand a portal user sees, after layering their Groups over the MSP default. The light colours are the
/// constructor's; dark colours and backgrounds are set by <see cref="PortalBranding.Resolve"/>, and default to automatic
/// dark colours on dotMARC's own backgrounds.</summary>
public sealed record ResolvedBrand(string ProductName, string Heading, string PrimaryColour, string SecondaryColour,
    Guid? LogoImageId, Guid? DarkLogoImageId, string? SupportEmail, string? SupportUrl, string? SupportPhone, string? FooterText)
{
    public string LightBackgroundColour { get; init; } = BrandColours.DefaultLightBackground;
    public string LightSurfaceColour { get; init; } = BrandColours.DefaultLightSurface;
    public string DarkBackgroundColour { get; init; } = BrandColours.DefaultDarkBackground;
    public string DarkSurfaceColour { get; init; } = BrandColours.DefaultDarkSurface;

    /// <summary>A dark colour someone chose, or null for automatic.</summary>
    public string? ExplicitDarkPrimaryColour { get; init; }
    public string? ExplicitDarkSecondaryColour { get; init; }

    public string DarkPrimaryColour => ExplicitDarkPrimaryColour ?? BrandColours.AutomaticDark(PrimaryColour, DarkSurfaceColour);
    public string DarkSecondaryColour => ExplicitDarkSecondaryColour ?? BrandColours.AutomaticDark(SecondaryColour, DarkSurfaceColour);
}
```

In `Resolve`, replace the `return new ResolvedBrand(...)` with:

```csharp
        return new ResolvedBrand(msp.ProductName, heading,
            groupBrand?.PrimaryColour ?? msp.PrimaryColour, groupBrand?.SecondaryColour ?? msp.SecondaryColour,
            logo, darkLogo, msp.SupportEmail, msp.SupportUrl, msp.SupportPhone, msp.FooterText)
        {
            ExplicitDarkPrimaryColour = DarkColour(groupBrand?.DarkPrimaryColour, groupBrand?.PrimaryColour, msp.DarkPrimaryColour),
            ExplicitDarkSecondaryColour = DarkColour(groupBrand?.DarkSecondaryColour, groupBrand?.SecondaryColour, msp.DarkSecondaryColour),
            LightBackgroundColour = msp.LightBackgroundColour ?? BrandColours.DefaultLightBackground,
            LightSurfaceColour = msp.LightSurfaceColour ?? BrandColours.DefaultLightSurface,
            DarkBackgroundColour = msp.DarkBackgroundColour ?? BrandColours.DefaultDarkBackground,
            DarkSurfaceColour = msp.DarkSurfaceColour ?? BrandColours.DefaultDarkSurface,
        };
    }

    /// <summary>A Group's own dark colour; else, when the Group has its own light colour, automatic from it (null); else
    /// the MSP's dark colour, which is null for automatic too. The MSP's dark colour never pairs with a Group's light one.</summary>
    private static string? DarkColour(string? groupDark, string? groupLight, string? mspDark) =>
        groupDark ?? (groupLight is not null ? null : mspDark);
```

- [ ] **Step 5: Generate the migration**

Run: `dotnet ef migrations add AddBrandColourModes --project src/DotMarc --startup-project src/DotMarc`
Expected: a new `*_AddBrandColourModes.cs` adding six nullable `character varying(7)` columns to `BrandingSettings` and two to `GroupBrandings`, and nothing else. Read it; if it touches anything else, stop and find out why.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalBrandingTests|FullyQualifiedName~PortalThemeTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Portal src/DotMarc/Data/DotMarcDbContext.cs src/DotMarc/Migrations test/DotMarc.Tests/Portal/PortalBrandingTests.cs
git commit -m "Store dark brand colours and backgrounds, and resolve them for the portal"
```

---

### Task 3: The portal theme

**Files:**
- Modify: `src/DotMarc/Portal/PortalTheme.cs`, `src/DotMarc/Portal/PortalBranding.cs` (`AppBarLogo`)
- Test: `test/DotMarc.Tests/Portal/PortalThemeTests.cs`, `test/DotMarc.Tests/Portal/PortalBrandingTests.cs`

**Interfaces:**
- Consumes: `ResolvedBrand` dark and background properties (Task 2)
- Produces: `PortalTheme.DarkAppBar(ResolvedBrand brand) -> string`; `PortalTheme.For` maps every brand colour

- [ ] **Step 1: Write the failing tests** (append to `PortalThemeTests`)

```csharp
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
```

Append to `PortalBrandingTests`:

```csharp
    [Fact]
    public void InDarkMode_ALightAppBar_GetsTheLightLogo()
    {
        var lightLogo = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var darkLogo = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var brand = new ResolvedBrand("Nova MSP", "Aurora Retail", "#0B5FFF", "#0B1B3F", lightLogo, darkLogo, null, null, null, null)
        {
            DarkBackgroundColour = "#F0F0F0",
        };

        Assert.Equal(lightLogo, PortalBranding.AppBarLogo(brand, darkMode: true));
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalThemeTests|FullyQualifiedName~PortalBrandingTests"`
Expected: build FAIL, `PortalTheme.DarkAppBar` is not defined.

- [ ] **Step 3: Implement**

In `PortalTheme`, add:

```csharp
    /// <summary>Dark mode's app bar and side menu: dotMARC's own on the default dark page, otherwise a step darker than
    /// the page, so a custom page colour never sits under dotMARC's navy.</summary>
    public static string DarkAppBar(ResolvedBrand brand) =>
        string.Equals(brand.DarkBackgroundColour, BrandColours.DefaultDarkBackground, StringComparison.OrdinalIgnoreCase)
            ? "#0F1724"
            : new MudBlazor.Utilities.MudColor(brand.DarkBackgroundColour).ColorDarken(0.03).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant();
```

Replace the body of `For` from `var darkPrimary = ...` to the end with:

```csharp
        theme.PaletteLight.Background = brand.LightBackgroundColour;
        theme.PaletteLight.Surface = brand.LightSurfaceColour;

        var darkPrimary = new MudBlazor.Utilities.MudColor(brand.DarkPrimaryColour);
        var darkSecondary = new MudBlazor.Utilities.MudColor(brand.DarkSecondaryColour);
        theme.PaletteDark.Primary = darkPrimary;
        theme.PaletteDark.PrimaryLighten = darkPrimary.ColorLighten(0.2).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.PrimaryDarken = darkPrimary.ColorDarken(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.PrimaryContrastText = ReadableTextOn(brand.DarkPrimaryColour);
        theme.PaletteDark.Secondary = darkSecondary;
        theme.PaletteDark.SecondaryLighten = darkSecondary.ColorLighten(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.SecondaryDarken = darkSecondary.ColorDarken(0.1).ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex);
        theme.PaletteDark.SecondaryContrastText = ReadableTextOn(brand.DarkSecondaryColour);
        theme.PaletteDark.Background = brand.DarkBackgroundColour;
        theme.PaletteDark.Surface = brand.DarkSurfaceColour;
        var darkAppBar = DarkAppBar(brand);
        theme.PaletteDark.AppbarBackground = darkAppBar;
        theme.PaletteDark.DrawerBackground = darkAppBar;
        theme.PaletteDark.AppbarText = ReadableTextOn(darkAppBar);
        theme.PaletteDark.DrawerText = ReadableTextOn(darkAppBar);
        theme.PaletteDark.DrawerIcon = ReadableTextOn(darkAppBar);
        return theme;
```

Delete the old dark block (the `ReadableOnDarkSurface` call and the two `PaletteDark.Secondary` lines). Update the `For` summary to: "The default look in the brand's colours: each mode's primary, secondary, page and card colours, and the light app bar in the secondary colour as dotMARC's own is."

In `PortalBranding.AppBarLogo`, replace the body with:

```csharp
        PortalTheme.IsDark(darkMode ? PortalTheme.DarkAppBar(brand) : brand.SecondaryColour) ? brand.DarkLogoImageId : brand.LogoImageId;
```

and its summary with: "The logo for the app bar: the dark logo whenever the bar is dark. Light mode's bar is the secondary colour and dark mode's follows the dark page colour, so either can be light or dark."

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalThemeTests|FullyQualifiedName~PortalBrandingTests"`
Expected: PASS, including the existing `TheAppBarLogo_SuitsTheAppBarsBackground` cases (the default dark app bar `#0F1724` is dark).

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Portal test/DotMarc.Tests/Portal
git commit -m "Theme the portal with each mode's brand colours and backgrounds"
```

---

### Task 4: Saving, validation and audit

**Files:**
- Modify: `src/DotMarc/Portal/BrandingSettingsService.cs`, `src/DotMarc/Data/GroupManagementService.cs:179-232`
- Test: `test/DotMarc.Tests/Portal/BrandingSettingsServiceTests.cs`, `test/DotMarc.Tests/Data/GroupManagementServiceTests.cs`

**Interfaces:**
- Consumes: the new `BrandingSettings`, `GroupBranding` and `GroupBrandingInput` fields (Task 2)

- [ ] **Step 1: Write the failing tests**

Append to `BrandingSettingsServiceTests`:

```csharp
    [Fact]
    public async Task Save_StoresDarkColoursAndBackgrounds_BlankAsNull_AndAuditsThem()
    {
        await using var context = CreateContext();
        var updated = await BrandingSettingsService.GetAsync(context);
        updated.DarkPrimaryColour = "#8ab4f8";
        updated.DarkSurfaceColour = "  ";

        await BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated);

        await using var verify = CreateContext();
        var saved = await BrandingSettingsService.GetAsync(verify);
        Assert.Equal(("#8AB4F8", null), (saved.DarkPrimaryColour, saved.DarkSurfaceColour));
        var entry = await verify.AuditEntries.SingleAsync();
        Assert.Contains(entry.Changes, change => change.Field == "Dark primary colour" && change.Old == "Automatic" && change.New == "#8AB4F8");
    }

    [Theory]
    [InlineData("DarkPrimaryColour", "Dark primary colour must be a hex colour such as #1A73E8.")]
    [InlineData("DarkSecondaryColour", "Dark secondary colour must be a hex colour such as #1A73E8.")]
    [InlineData("LightBackgroundColour", "Light page background must be a hex colour such as #1A73E8.")]
    [InlineData("LightSurfaceColour", "Light card background must be a hex colour such as #1A73E8.")]
    [InlineData("DarkBackgroundColour", "Dark page background must be a hex colour such as #1A73E8.")]
    [InlineData("DarkSurfaceColour", "Dark card background must be a hex colour such as #1A73E8.")]
    public async Task Save_RefusesAnInvalidNewColour(string property, string message)
    {
        await using var context = CreateContext();
        var updated = await BrandingSettingsService.GetAsync(context);
        typeof(BrandingSettings).GetProperty(property)!.SetValue(updated, "blue");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => BrandingSettingsService.SaveAsync(context, TestActors.Admin, updated));

        Assert.StartsWith(message, exception.Message);
    }
```

Append to `GroupManagementServiceTests`:

```csharp
    [Fact]
    public async Task SetBrandingAsync_KeepsAGroupWithOnlyDarkColours_AndAuditsThem()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        await GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id,
            new GroupBrandingInput(null, null, null, null, null, DarkPrimaryColour: "#ce93d8", DarkSecondaryColour: null));

        await using var verify = CreateContext();
        var saved = await GroupManagementService.GetBrandingAsync(verify, group.Id);
        Assert.Equal("#CE93D8", saved!.DarkPrimaryColour);
        Assert.Contains((await verify.AuditEntries.SingleAsync()).Changes, change => change.Field == "Dark primary colour" && change.New == "#CE93D8");
    }

    [Fact]
    public async Task SetBrandingAsync_RefusesAnInvalidDarkColour()
    {
        await using var context = CreateContext();
        var group = new Group { Name = "Aurora Retail" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => GroupManagementService.SetBrandingAsync(context, TestActors.Admin, group.Id,
            new GroupBrandingInput(null, null, null, null, null, DarkSecondaryColour: "teal")));

        Assert.StartsWith("Dark secondary colour must be a hex colour such as #1A73E8.", exception.Message);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandingSettingsServiceTests|FullyQualifiedName~SetBrandingAsync"`
Expected: FAIL: the dark colour is saved lower case with no audit field, invalid colours are accepted, and the dark-only Group's row isn't kept.

- [ ] **Step 3: Implement `BrandingSettingsService`**

In `Normalise`, add:

```csharp
        settings.DarkPrimaryColour = Colour(settings.DarkPrimaryColour);
        settings.DarkSecondaryColour = Colour(settings.DarkSecondaryColour);
        settings.LightBackgroundColour = Colour(settings.LightBackgroundColour);
        settings.LightSurfaceColour = Colour(settings.LightSurfaceColour);
        settings.DarkBackgroundColour = Colour(settings.DarkBackgroundColour);
        settings.DarkSurfaceColour = Colour(settings.DarkSurfaceColour);
```

with the helper:

```csharp
    private static string? Colour(string? value) => Blank(value)?.ToUpperInvariant();
```

In `Validate`, after the secondary colour line:

```csharp
        ValidateOptionalColour(settings.DarkPrimaryColour, "Dark primary colour");
        ValidateOptionalColour(settings.DarkSecondaryColour, "Dark secondary colour");
        ValidateOptionalColour(settings.LightBackgroundColour, "Light page background");
        ValidateOptionalColour(settings.LightSurfaceColour, "Light card background");
        ValidateOptionalColour(settings.DarkBackgroundColour, "Dark page background");
        ValidateOptionalColour(settings.DarkSurfaceColour, "Dark card background");
```

with:

```csharp
    private static void ValidateOptionalColour(string? colour, string name)
    {
        if (colour is not null && !BrandColours.IsValid(colour))
        {
            throw new ArgumentException($"{name} must be a hex colour such as #1A73E8.", "settings");
        }
    }
```

In `SaveAsync`, add after the secondary colour audit field:

```csharp
            .Field("Dark primary colour", saved.DarkPrimaryColour ?? "Automatic", updated.DarkPrimaryColour ?? "Automatic")
            .Field("Dark secondary colour", saved.DarkSecondaryColour ?? "Automatic", updated.DarkSecondaryColour ?? "Automatic")
            .Field("Light page background", saved.LightBackgroundColour ?? "Default", updated.LightBackgroundColour ?? "Default")
            .Field("Light card background", saved.LightSurfaceColour ?? "Default", updated.LightSurfaceColour ?? "Default")
            .Field("Dark page background", saved.DarkBackgroundColour ?? "Default", updated.DarkBackgroundColour ?? "Default")
            .Field("Dark card background", saved.DarkSurfaceColour ?? "Default", updated.DarkSurfaceColour ?? "Default")
```

- [ ] **Step 4: Implement `GroupManagementService.SetBrandingAsync`**

After `secondaryColour` is read:

```csharp
        var darkPrimaryColour = Blank(input.DarkPrimaryColour)?.ToUpperInvariant();
        var darkSecondaryColour = Blank(input.DarkSecondaryColour)?.ToUpperInvariant();
```

After the secondary colour validation:

```csharp
        if (darkPrimaryColour is not null && !BrandColours.IsValid(darkPrimaryColour)) throw new ArgumentException("Dark primary colour must be a hex colour such as #1A73E8.", nameof(input));
        if (darkSecondaryColour is not null && !BrandColours.IsValid(darkSecondaryColour)) throw new ArgumentException("Dark secondary colour must be a hex colour such as #1A73E8.", nameof(input));
```

Add to `changes`:

```csharp
            .Field("Dark primary colour", existing?.DarkPrimaryColour, darkPrimaryColour)
            .Field("Dark secondary colour", existing?.DarkSecondaryColour, darkSecondaryColour);
```

(moving the `;` from the secondary colour line). Add `&& darkPrimaryColour is null && darkSecondaryColour is null` to `cleared`, and after `existing.SecondaryColour = secondaryColour;`:

```csharp
            existing.DarkPrimaryColour = darkPrimaryColour;
            existing.DarkSecondaryColour = darkSecondaryColour;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~BrandingSettingsServiceTests|FullyQualifiedName~GroupManagementServiceTests|FullyQualifiedName~AuditCoverageTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Portal/BrandingSettingsService.cs src/DotMarc/Data/GroupManagementService.cs test/DotMarc.Tests/Portal/BrandingSettingsServiceTests.cs test/DotMarc.Tests/Data/GroupManagementServiceTests.cs
git commit -m "Validate, save and audit dark brand colours and backgrounds"
```

---

### Task 5: Secondary colour accents in the portal

**Files:**
- Create: `src/DotMarc/Portal/PortalHealthColours.cs`
- Modify: `src/DotMarc/Components/Portal/PortalHealthChip.razor`, `PortalTrendLine.razor`, `PortalDomainView.razor`, `src/DotMarc/Components/Pages/Portal/PortalHome.razor`, `src/DotMarc/Components/Layout/PortalLayout.razor`
- Test: `test/DotMarc.Tests/Portal/PortalHealthColoursTests.cs`

**Interfaces:**
- Produces: `PortalHealthColours.For(PortalHealth health) -> MudBlazor.Color`

- [ ] **Step 1: Write the failing test**

```csharp
using DotMarc.Portal;
using MudBlazor;
using Xunit;

namespace DotMarc.Tests.Portal;

public sealed class PortalHealthColoursTests
{
    [Theory]
    [InlineData(PortalHealth.Protected, Color.Success)]
    [InlineData(PortalHealth.NeedsAttention, Color.Warning)]
    [InlineData(PortalHealth.MonitoringOnly, Color.Secondary)]
    [InlineData(PortalHealth.NoReportsYet, Color.Secondary)]
    public void StatusesKeepTheirMeaning_AndTheNeutralOnesUseTheBrand(PortalHealth health, Color expected) =>
        Assert.Equal(expected, PortalHealthColours.For(health));
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalHealthColoursTests"`
Expected: build FAIL, `PortalHealthColours` is not defined.

- [ ] **Step 3: Implement**

`src/DotMarc/Portal/PortalHealthColours.cs`:

```csharp
using MudBlazor;

namespace DotMarc.Portal;

/// <summary>The colour of a domain's status in the portal. Protected and Needs attention keep green and amber for what
/// they mean; the neutral statuses take the brand's secondary colour.</summary>
public static class PortalHealthColours
{
    public static Color For(PortalHealth health) => health switch
    {
        PortalHealth.Protected => Color.Success,
        PortalHealth.NeedsAttention => Color.Warning,
        _ => Color.Secondary,
    };
}
```

In `PortalHealthChip.razor`, replace the `Colour` switch with `private Color Colour => PortalHealthColours.For(Health);`.

In `PortalTrendLine.razor`, change `stroke="var(--mud-palette-primary)"` to `stroke="var(--mud-palette-secondary)"`.

Add `Color="Color.Secondary"` to these headings:
- `PortalHome.razor`: `<MudText Typo="Typo.h4" Class="mb-1">Your domains</MudText>`
- `PortalDomainView.razor`: the `Typo.h4` domain name and the four `Typo.h6` section headings (Policy, Health, Who sends as this domain, Alerts).

In `PortalLayout.razor`'s footer, add `Color="Color.Secondary"` to the two `MudLink`s.

- [ ] **Step 4: Run the test and the build**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~PortalHealthColoursTests|FullyQualifiedName~Portal"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Portal/PortalHealthColours.cs src/DotMarc/Components test/DotMarc.Tests/Portal/PortalHealthColoursTests.cs
git commit -m "Use the secondary colour for portal headings, the trend line, footer links and neutral statuses"
```

---

### Task 6: Secondary colour in the PDF report and email

**Files:**
- Modify: `src/DotMarc/Reporting/ClientReports/ClientReportDocument.cs`, `ClientReportEmail.cs`
- Test: `test/DotMarc.Tests/Reporting/ClientReports/ClientReportDocumentTests.cs`, `ClientReportEmailTests.cs`

**Interfaces:**
- Consumes: `ResolvedBrand.SecondaryColour` (existing), `PortalTheme.ReadableTextOn` (existing)

- [ ] **Step 1: Write the failing tests**

Append to `ClientReportDocumentTests` (sample brand secondary is `#FF6B00`; add `using MigraDoc.DocumentObjectModel;` and `using MigraDoc.DocumentObjectModel.Tables;`):

```csharp
    [Fact]
    public void SectionHeadings_AndTableHeaders_UseTheSecondaryColour()
    {
        var document = ClientReportDocument.Build(SampleReport());
        var secondary = new MigraDoc.DocumentObjectModel.Color(0xFF, 0x6B, 0x00);

        Assert.Equal(secondary, document.Styles[StyleNames.Heading2]!.Font.Color);
        var headers = document.Sections[0].Elements.OfType<Table>().Select(table => table.Rows[0]).ToList();
        Assert.NotEmpty(headers);
        Assert.All(headers, header => Assert.Equal(secondary, header.Shading.Color));
    }
```

Append to `ClientReportEmailTests`:

```csharp
    [Fact]
    public void TheHeading_AndTheTableHeader_UseTheSecondaryColour()
    {
        var message = ClientReportEmail.Compose(ClientReportDocumentTests.SampleReport(), [0x25, 0x50], ["it@aurora-retail.example"]);

        Assert.Contains("<h1 style=\"font-size:20px;margin:0 0 4px;color:#FF6B00\">", message.HtmlBody);
        Assert.Contains("<tr style=\"text-align:left;background:#FF6B00;color:#000000\">", message.HtmlBody);
    }
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReportDocumentTests|FullyQualifiedName~ClientReportEmailTests"`
Expected: FAIL on the two new tests.

- [ ] **Step 3: Implement the PDF**

In `Build`, after `var primary = ...`: `var secondary = Hex(report.Brand.SecondaryColour);` and set `document.Styles[StyleNames.Heading2]!.Font.Color = secondary;`.

Give `NewTable` the brand: change its signature to `NewTable(Section section, ResolvedBrand brand, params (string Header, double WidthCm)[] columns)`, pass `report.Brand` from the summary call and the brand down to `AddDomain` for the health and sender tables (add a `ResolvedBrand brand` parameter to `AddDomain` and pass `report.Brand` from `Build`). In `NewTable`, after `header.Format.Font.Bold = true;`:

```csharp
        // The header row in the brand's secondary colour, with whichever of near-white or black reads on it.
        header.Shading.Color = Hex(brand.SecondaryColour);
        header.Format.Font.Color = Hex(PortalTheme.ReadableTextOn(brand.SecondaryColour));
```

`PortalTheme.ReadableTextOn` returns `#FCFCFC` or `#000000`, which `Hex` reads.

- [ ] **Step 4: Implement the email**

In `ClientReportEmail.Compose`, change the heading line to:

```csharp
        html.Append($"<h1 style=\"font-size:20px;margin:0 0 4px;color:{E(report.Brand.SecondaryColour)}\">{E(report.Brand.Heading)}</h1>");
```

and the table header row's `<tr style=\"text-align:left\">` to:

```csharp
<tr style=\"text-align:left;background:{E(report.Brand.SecondaryColour)};color:{E(PortalTheme.ReadableTextOn(report.Brand.SecondaryColour))}\">
```

(the string becomes interpolated; add `using DotMarc.Portal;` if missing).

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~ClientReports"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/DotMarc/Reporting/ClientReports test/DotMarc.Tests/Reporting/ClientReports
git commit -m "Use the secondary colour for report section headings and table headers, and in the email"
```

---

### Task 7: The colour input, branding page, Group dialog and previews

**Files:**
- Modify: `src/DotMarc/Components/Portal/BrandColourField.razor`, `PortalBrandPreview.razor`
- Modify: `src/DotMarc/Components/Pages/BrandingSettingsPage.razor`, `src/DotMarc/Components/Dialogs/GroupBrandingDialog.razor`

**Interfaces:**
- Consumes: Tasks 1 to 3 (`BrandColours.RatingText`, `RatioOnBackgrounds`, `Rate`, `LightModeText`, `DarkModeText`, `ResolvedBrand` properties, `PortalTheme.DarkAppBar`, `PortalBranding.Resolve`)
- Produces: `BrandColourField` parameters `Label`, `Help`, `Value`, `ValueChanged`, `DefaultColour`, `DefaultHint`, `ResetLabel`, `FallbackColour`, `Background`, `Surface`, `TextColour`

This task is UI only and is verified in the browser (Step 6); its logic lives in Tasks 1 to 3, which are unit tested.

- [ ] **Step 1: Rework `BrandColourField.razor`**

```razor
@using DotMarc.Components.Shared
@using DotMarc.Portal
@using MudBlazor.Utilities

@* A brand colour: a swatch, a picker whose hex can be typed or pasted, and its WCAG rating. A brand colour is rated on the
   page and card it sits on (Background and Surface); a background is rated with the mode's text on it (TextColour).
   With a DefaultColour the field is optional: left blank, the default is used, and the reset button returns to it. *@
<FieldWithHelp Topic="@Label" Help="@Help">
    <div class="d-flex align-start" style="gap: 0.5rem;">
        <div aria-hidden="true" style="@($"background: {EffectiveColour}; width: 40px; height: 40px; margin-top: 8px; border-radius: 4px; border: 1px solid rgba(127,127,127,0.5); flex-shrink: 0;")"></div>
        <MudColorPicker Label="@Label" Value="@(new MudColor(EffectiveColour))" ValueChanged="@((MudColor colour) => ValueChanged.InvokeAsync(colour.ToString(MudColorOutputFormats.Hex).ToUpperInvariant()))"
                        ShowAlpha="false" ColorPickerMode="ColorPickerMode.HEX" Editable="true" Variant="Variant.Outlined" Class="flex-grow-1"
                        HelperText="@(DefaultColour is not null && !HasOwnValue ? $"{DefaultHint} ({DefaultColour})" : null)" />
    </div>
</FieldWithHelp>
@if (DefaultColour is not null && HasOwnValue)
{
    <MudButton Variant="Variant.Text" Size="Size.Small" OnClick="@(() => ValueChanged.InvokeAsync(null))">@ResetLabel</MudButton>
}
@if (Ratio is { } ratio)
{
    <div class="d-flex align-center flex-wrap mt-1" style="gap: 0.5rem;">
        <MudChip T="string" Size="Size.Small" Variant="Variant.Outlined" Color="@(BrandColours.Rate(ratio) >= ContrastRating.AA ? Color.Success : Color.Warning)">
            @BrandColours.RatingText(ratio)
        </MudChip>
        <MudText Typo="Typo.caption" Class="mud-text-secondary">@(TextColour is null ? "on the page and cards" : "for text")</MudText>
    </div>
    @if (BrandColours.Rate(ratio) < ContrastRating.AA)
    {
        <MudText Typo="Typo.caption" Class="d-block mt-1" Style="color: var(--mud-palette-warning-text);">
            Text @(TextColour is null ? "in this colour" : "on this colour") may be hard for some people to read. You can still use it.
        </MudText>
    }
}

@code {
    [Parameter, EditorRequired] public string Label { get; set; } = "";
    [Parameter, EditorRequired] public string Help { get; set; } = "";
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }

    /// <summary>The colour used while the field is blank. Null for a required field.</summary>
    [Parameter] public string? DefaultColour { get; set; }

    /// <summary>Says where a blank field's colour comes from, such as "Using the MSP's" or "Automatic, from the light primary".</summary>
    [Parameter] public string DefaultHint { get; set; } = "Using the MSP's";

    [Parameter] public string ResetLabel { get; set; } = "Use the MSP default";

    /// <summary>Used when nothing valid is set and there's no default, so the picker always has a colour to show.</summary>
    [Parameter] public string FallbackColour { get; set; } = BrandingSettings.DefaultPrimaryColour;

    /// <summary>The page and card colours a brand colour is rated against.</summary>
    [Parameter] public string? Background { get; set; }
    [Parameter] public string? Surface { get; set; }

    /// <summary>For a background: the mode's text colour, rated on this colour.</summary>
    [Parameter] public string? TextColour { get; set; }

    private bool HasOwnValue => BrandColours.IsValid(Value);

    private string EffectiveColour => HasOwnValue ? Value! : DefaultColour ?? FallbackColour;

    private double? Ratio =>
        TextColour is not null ? BrandColours.ContrastRatio(TextColour, EffectiveColour)
        : BrandColours.IsValid(Background) && BrandColours.IsValid(Surface) ? BrandColours.RatioOnBackgrounds(EffectiveColour, Background!, Surface!)
        : null;
}
```

- [ ] **Step 2: The branding page**

In `BrandingSettingsPage.razor`, replace the two colour `MudItem`s with a light column and a dark column, and add the Advanced panel after the support fields' `MudItem`s (inside the `MudGrid`):

```razor
            <MudItem xs="12" md="6">
                <MudText Typo="Typo.subtitle1" Class="mb-2">Light mode</MudText>
                <BrandColourField Label="Primary colour" Help="Links, buttons and highlights in light mode."
                                  Value="@_settings.PrimaryColour" ValueChanged="@(colour => _settings.PrimaryColour = colour ?? "")"
                                  FallbackColour="@BrandingSettings.DefaultPrimaryColour"
                                  Background="@Preview.LightBackgroundColour" Surface="@Preview.LightSurfaceColour" />
                <div class="mt-4">
                    <BrandColourField Label="Secondary colour" Help="Light mode's app bar, and headings, the pass rate line, footer links and neutral statuses in the portal and reports."
                                      Value="@_settings.SecondaryColour" ValueChanged="@(colour => _settings.SecondaryColour = colour ?? "")"
                                      FallbackColour="@BrandingSettings.DefaultSecondaryColour"
                                      Background="@Preview.LightBackgroundColour" Surface="@Preview.LightSurfaceColour" />
                </div>
            </MudItem>
            <MudItem xs="12" md="6">
                <MudText Typo="Typo.subtitle1" Class="mb-2">Dark mode</MudText>
                <BrandColourField Label="Dark primary colour" Help="Links, buttons and highlights in dark mode. Left automatic, it's the light primary, lightened until it reads on the dark cards."
                                  @bind-Value="_settings.DarkPrimaryColour" DefaultColour="@AutomaticDarkPrimary"
                                  DefaultHint="Automatic, from the light primary" ResetLabel="Use automatic"
                                  Background="@Preview.DarkBackgroundColour" Surface="@Preview.DarkSurfaceColour" />
                <div class="mt-4">
                    <BrandColourField Label="Dark secondary colour" Help="The secondary colour's accents in dark mode. Left automatic, it's the light secondary, lightened until it reads on the dark cards."
                                      @bind-Value="_settings.DarkSecondaryColour" DefaultColour="@AutomaticDarkSecondary"
                                      DefaultHint="Automatic, from the light secondary" ResetLabel="Use automatic"
                                      Background="@Preview.DarkBackgroundColour" Surface="@Preview.DarkSurfaceColour" />
                </div>
            </MudItem>
```

```razor
            <MudItem xs="12">
                <MudExpansionPanels Elevation="0" Outlined="true">
                    <MudExpansionPanel Text="Advanced: backgrounds">
                        <MudText Typo="Typo.body2" Class="mud-text-secondary mb-3">The portal's page and card colours in each mode. They apply to every client.</MudText>
                        <MudGrid>
                            <MudItem xs="12" md="6">
                                <BrandColourField Label="Light page background" Help="Behind everything in light mode." @bind-Value="_settings.LightBackgroundColour"
                                                  DefaultColour="@BrandColours.DefaultLightBackground" DefaultHint="Using the default" ResetLabel="Use the default" TextColour="@BrandColours.LightModeText" />
                                <div class="mt-4">
                                    <BrandColourField Label="Light card background" Help="Cards and panels in light mode." @bind-Value="_settings.LightSurfaceColour"
                                                      DefaultColour="@BrandColours.DefaultLightSurface" DefaultHint="Using the default" ResetLabel="Use the default" TextColour="@BrandColours.LightModeText" />
                                </div>
                            </MudItem>
                            <MudItem xs="12" md="6">
                                <BrandColourField Label="Dark page background" Help="Behind everything in dark mode. The app bar is a step darker." @bind-Value="_settings.DarkBackgroundColour"
                                                  DefaultColour="@BrandColours.DefaultDarkBackground" DefaultHint="Using the default" ResetLabel="Use the default" TextColour="@BrandColours.DarkModeText" />
                                <div class="mt-4">
                                    <BrandColourField Label="Dark card background" Help="Cards and panels in dark mode." @bind-Value="_settings.DarkSurfaceColour"
                                                      DefaultColour="@BrandColours.DefaultDarkSurface" DefaultHint="Using the default" ResetLabel="Use the default" TextColour="@BrandColours.DarkModeText" />
                                </div>
                            </MudItem>
                        </MudGrid>
                    </MudExpansionPanel>
                </MudExpansionPanels>
            </MudItem>
```

Replace the `PreviewBrand` property in `@code` with a resolved preview, and add the automatic helpers:

```csharp
    // Resolved exactly as a client with one unbranded Group would see it, with the unsaved fields; anything not yet
    // valid falls back, so typing a colour never breaks the preview.
    private ResolvedBrand Preview
    {
        get
        {
            var settings = _settings!;
            static string? Valid(string? colour) => BrandColours.IsValid(colour) ? colour : null;
            var unsaved = new BrandingSettings
            {
                ProductName = string.IsNullOrWhiteSpace(settings.ProductName) ? "dotMARC" : settings.ProductName.Trim(),
                PrimaryColour = Valid(settings.PrimaryColour) ?? BrandingSettings.DefaultPrimaryColour,
                SecondaryColour = Valid(settings.SecondaryColour) ?? BrandingSettings.DefaultSecondaryColour,
                DarkPrimaryColour = Valid(settings.DarkPrimaryColour),
                DarkSecondaryColour = Valid(settings.DarkSecondaryColour),
                LightBackgroundColour = Valid(settings.LightBackgroundColour),
                LightSurfaceColour = Valid(settings.LightSurfaceColour),
                DarkBackgroundColour = Valid(settings.DarkBackgroundColour),
                DarkSurfaceColour = Valid(settings.DarkSurfaceColour),
                LogoImageId = settings.LogoImageId,
                DarkLogoImageId = settings.DarkLogoImageId,
                SupportEmail = settings.SupportEmail,
                SupportUrl = settings.SupportUrl,
                SupportPhone = settings.SupportPhone,
                FooterText = settings.FooterText,
            };
            return PortalBranding.Resolve(unsaved, [new ScopedGroupBrand("Example client", null)]);
        }
    }

    private string AutomaticDarkPrimary => BrandColours.AutomaticDark(Preview.PrimaryColour, Preview.DarkSurfaceColour);
    private string AutomaticDarkSecondary => BrandColours.AutomaticDark(Preview.SecondaryColour, Preview.DarkSurfaceColour);
```

and change `<PortalBrandPreview Brand="PreviewBrand" />` to `<PortalBrandPreview Brand="Preview" />`.

- [ ] **Step 3: The Group dialog**

In `GroupBrandingDialog.razor`, add `private string? _darkPrimaryColour;` and `private string? _darkSecondaryColour;`, load them in `OnInitializedAsync` (`_darkPrimaryColour = branding?.DarkPrimaryColour;` and the same for secondary), and change `Input` to:

```csharp
    private GroupBrandingInput Input => new(_displayName, _logoImageId, _darkLogoImageId, _primaryColour, _secondaryColour, _darkPrimaryColour, _darkSecondaryColour);
```

In `PreviewBrand`'s `unsaved`, add:

```csharp
                DarkPrimaryColour = BrandColours.IsValid(input.DarkPrimaryColour) ? input.DarkPrimaryColour : null,
                DarkSecondaryColour = BrandColours.IsValid(input.DarkSecondaryColour) ? input.DarkSecondaryColour : null,
```

Add, for the dark fields' defaults (what this Group's portal uses while the field is blank):

```csharp
    private ResolvedBrand WithoutOwnDarkColours => PreviewBrand with { ExplicitDarkPrimaryColour = DarkFallback(_primaryColour, _msp!.DarkPrimaryColour), ExplicitDarkSecondaryColour = DarkFallback(_secondaryColour, _msp!.DarkSecondaryColour) };

    // Mirrors PortalBranding.Resolve with this Group's dark colour blank: automatic from the Group's light colour, else
    // the MSP's dark colour.
    private static string? DarkFallback(string? groupLight, string? mspDark) => BrandColours.IsValid(groupLight) ? null : mspDark;

    private string DarkHint(string? groupLight, string? mspDark, string which) =>
        BrandColours.IsValid(groupLight) ? $"Automatic, from this Group's {which}" : mspDark is null ? $"Automatic, from the MSP's {which}" : "Using the MSP's";
```

Replace the two colour `MudItem`s with:

```razor
                <MudItem xs="12" md="6">
                    <MudText Typo="Typo.subtitle1" Class="mb-2">Light mode</MudText>
                    <BrandColourField Label="Primary colour" Help="Links, buttons and highlights in this client's portal in light mode."
                                      @bind-Value="_primaryColour" DefaultColour="@_msp.PrimaryColour"
                                      Background="@PreviewBrand.LightBackgroundColour" Surface="@PreviewBrand.LightSurfaceColour" />
                    <div class="mt-4">
                        <BrandColourField Label="Secondary colour" Help="Light mode's app bar, and headings and accents in this client's portal and reports."
                                          @bind-Value="_secondaryColour" DefaultColour="@_msp.SecondaryColour"
                                          Background="@PreviewBrand.LightBackgroundColour" Surface="@PreviewBrand.LightSurfaceColour" />
                    </div>
                </MudItem>
                <MudItem xs="12" md="6">
                    <MudText Typo="Typo.subtitle1" Class="mb-2">Dark mode</MudText>
                    <BrandColourField Label="Dark primary colour" Help="Links, buttons and highlights in this client's portal in dark mode."
                                      @bind-Value="_darkPrimaryColour" DefaultColour="@WithoutOwnDarkColours.DarkPrimaryColour"
                                      DefaultHint="@DarkHint(_primaryColour, _msp.DarkPrimaryColour, "primary")" ResetLabel="Clear"
                                      Background="@PreviewBrand.DarkBackgroundColour" Surface="@PreviewBrand.DarkSurfaceColour" />
                    <div class="mt-4">
                        <BrandColourField Label="Dark secondary colour" Help="The secondary colour's accents in this client's portal in dark mode."
                                          @bind-Value="_darkSecondaryColour" DefaultColour="@WithoutOwnDarkColours.DarkSecondaryColour"
                                          DefaultHint="@DarkHint(_secondaryColour, _msp.DarkSecondaryColour, "secondary")" ResetLabel="Clear"
                                          Background="@PreviewBrand.DarkBackgroundColour" Surface="@PreviewBrand.DarkSurfaceColour" />
                    </div>
                </MudItem>
```

- [ ] **Step 4: The preview**

In `PortalBrandPreview.razor`, replace `ContainerStyle` and `AppBarStyle` with:

```csharp
    private string ContainerStyle(bool dark)
    {
        var (background, surface, primary, secondary) = dark
            ? (Brand.DarkBackgroundColour, Brand.DarkSurfaceColour, Brand.DarkPrimaryColour, Brand.DarkSecondaryColour)
            : (Brand.LightBackgroundColour, Brand.LightSurfaceColour, Brand.PrimaryColour, Brand.SecondaryColour);
        var text = dark
            ? "--mud-palette-text-primary: #e8eaf0; --mud-palette-text-secondary: #adb5c7; --mud-palette-lines-default: rgba(255,255,255,0.08);"
            : "--mud-palette-text-primary: rgba(66,66,66,1); --mud-palette-text-secondary: #616161;";
        return $"{text} --mud-palette-surface: {surface}; --mud-palette-background: {background}; background: {background}; " +
               $"--mud-palette-primary: {primary}; --mud-palette-secondary: {secondary}; " +
               "color: var(--mud-palette-text-primary); border-radius: 8px; overflow: hidden; min-width: 280px; border: 1px solid rgba(127,127,127,0.3);";
    }

    private string AppBarStyle(bool dark)
    {
        var background = dark ? PortalTheme.DarkAppBar(Brand) : Brand.SecondaryColour;
        return $"background: {background}; color: {PortalTheme.ReadableTextOn(background)}; height: 56px;";
    }
```

Change the sample domain's health from `PortalHealth.Protected` to `PortalHealth.MonitoringOnly`, so the preview shows a secondary-coloured status chip and the secondary trend line together.

- [ ] **Step 5: Build and run the portal tests**

Run: `dotnet build dotMARC.sln` then `dotnet test test/DotMarc.Tests --filter "FullyQualifiedName~Portal"`
Expected: build succeeds with no new warnings; tests PASS.

- [ ] **Step 6: Browser check (playwright-edge)**

Start the demo (`dotnet run --project src/DotMarc` with the demo settings used before), sign in as the demo admin, and check:
1. Manage > Branding: light and dark columns, each field with a swatch and a rating chip; the dark fields say "Automatic, from the light primary" and show the worked-out colour.
2. Type an invalid colour into the primary field: the preview keeps working.
3. Open "Advanced: backgrounds", set the dark page background to `#F0F0F0`: the dark preview's app bar turns light with dark text, and the dark colour ratings change.
4. Pick a dark primary, then **Use automatic** returns it.
5. Manage groups > Branding for Aurora Retail: light and dark columns; the dark hints read "Automatic, from this Group's primary".
6. Resize to 390px wide: the columns stack and nothing scrolls sideways.
Fix anything wrong before committing.

- [ ] **Step 7: Commit**

```bash
git add src/DotMarc/Components
git commit -m "Show a swatch and WCAG rating on brand colours, with dark colours and backgrounds on the branding page"
```

---

### Task 8: Demo, docs and the whole suite

**Files:**
- Modify: `src/DotMarc/Demo/DemoDataSeeder.cs:288-301`
- Modify: `website/docs/client-portal.mdx` (the Branding section, lines 54 to 90)

- [ ] **Step 1: Demo seeder**

After `brand.SecondaryColour = "#FF6B00";`:

```csharp
        brand.DarkPrimaryColour = null;
        brand.DarkSecondaryColour = null;
        brand.LightBackgroundColour = null;
        brand.LightSurfaceColour = null;
        brand.DarkBackgroundColour = null;
        brand.DarkSurfaceColour = null;
```

and give Aurora Retail's `GroupBranding` its own dark primary: `DarkPrimaryColour = "#CE93D8",`.

- [ ] **Step 2: Docs**

In `website/docs/client-portal.mdx`, replace the colour bullets and the contrast paragraph (lines 60, 61 and 69 to 70) with:

```mdx
- **Light mode** and **Dark mode** each have a **primary colour** (links, buttons and highlights) and a **secondary
  colour** (headings, the pass rate line, footer links and neutral statuses such as "Monitoring only", and light mode's
  app bar). Client reports use the light colours: the primary for the title, the secondary for section headings and
  table headers.
- Dark colours start **automatic**: the light colour, lightened until it's easy to read on dark mode's cards. Pick one to
  use your own, or **Use automatic** to go back.
- **Advanced: backgrounds** sets the page and card colours for each mode, for every client.

Each colour shows its contrast rating against the page and cards it sits on, using WCAG's levels: **AAA** (7 to 1 or
more), **AA** (4.5 to 1, the usual target for text), and **AA large text only** (3 to 1, fine for headings but not body
text). A background shows how readable normal text is on it. A colour below AA can still be saved, so you can use a
brand's official colours, but text in it may be hard for some people to read.
```

In "Branding a Group", change "sets that client's own display name, logo, dark logo and colours" to "sets that client's own display name, logo, dark logo, and light and dark colours". Add after the Group paragraph: "A Group's dark colours left empty are automatic from its own light colours; if it has no light colours of its own, it uses yours. Backgrounds are always yours."

Check there are no em dashes: `grep -c "—" website/docs/client-portal.mdx` returns 0.

- [ ] **Step 3: Build the docs**

Run (from `website`): `npx docusaurus build --out-dir <scratch>/site`
Expected: success, no broken links.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test dotMARC.sln`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/DotMarc/Demo/DemoDataSeeder.cs website/docs/client-portal.mdx
git commit -m "Reset the new brand colours in the demo, and document light and dark colours, backgrounds and contrast"
```

The branding screenshots are regenerated by the `update-screenshots` workflow after merge; run it then.
