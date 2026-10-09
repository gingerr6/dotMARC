# Brand colours: light and dark, backgrounds and contrast ratings

**Release:** dotMARC 0.9.2
**Status:** approved in brainstorming, awaiting spec review

## Purpose

Branding today is one primary and one secondary colour. Dark mode derives everything from them, the secondary only colours light mode's app bar, backgrounds are fixed, and the only contrast feedback is a warning when a colour is hard to read on white. This lets an MSP (and each client Group) set the colours each mode really uses, choose the portal's backgrounds, see each colour and its WCAG rating as they pick it, and gives the secondary colour a real job in the portal and the client reports.

## Decisions made in brainstorming

- **The secondary colour is an accent** in the portal (headings, the trend line, footer links, neutral status chips) and in the PDF report and email (section headings, table headers). It is not used for dark mode's app bar.
- **Four brand colours up front:** light primary, light secondary, dark primary, dark secondary. The dark colours default to the automatically adjusted light ones and can be set explicitly.
- **Backgrounds in an "Advanced" section:** page and card background for light and for dark. Backgrounds are MSP-wide.
- **Groups override the four brand colours**, never the backgrounds.
- **Contrast is advisory.** Each colour shows its WCAG rating; a colour below AA can still be saved, because a brand's official colours sometimes fail and an MSP must be able to use them. Nothing is silently adjusted except a dark colour left on automatic, which is visible and overridable.
- **Status colours keep their meaning.** "Protected" stays green and "Needs attention" amber; only the neutral states take the secondary colour.

## Scope

**In:** the new stored colours and their resolution; the reworked colour input (swatch and rating); the branding page and Group dialog layouts; the portal theme, portal accents, PDF and email changes; validation, audit, migration, demo, docs and tests.

**Out:** theming dotMARC's own staff interface; configurable text colours; per-Group backgrounds; enforcing a minimum contrast; colours in the API.

## 1. Stored colours and resolution

### `BrandingSettings` (MSP, singleton)

| Column | Notes |
| --- | --- |
| `PrimaryColour`, `SecondaryColour` | Existing. Now the light-mode colours. |
| `DarkPrimaryColour`, `DarkSecondaryColour` | New, nullable. Null means automatic. |
| `LightBackgroundColour`, `LightSurfaceColour` | New, nullable. Null means the theme's default light page and card colours. |
| `DarkBackgroundColour`, `DarkSurfaceColour` | New, nullable. Null means `#111827` and `#1E2A3A`. |

### `GroupBranding`

Adds nullable `DarkPrimaryColour` and `DarkSecondaryColour`. `GroupBrandingInput` gains both. The row's "exists while at least one field is set" rule counts them.

### `ResolvedBrand`

Carries `PrimaryColour`, `SecondaryColour`, `DarkPrimaryColour`, `DarkSecondaryColour` (always resolved, never null), and the four background colours (resolved to the defaults when unset).

### Resolving a dark colour (primary and secondary alike)

1. The single branded Group's own dark colour, when set.
2. Otherwise, when that Group set its own light colour, the automatic dark version of that light colour. The MSP's dark colour is never paired with a Group's light colour, as with logos.
3. Otherwise, the MSP's dark colour, when set.
4. Otherwise, the automatic dark version of the resolved light colour.

**Automatic** is today's rule for the primary, applied to both colours: lighten in steps until the colour meets AA for normal text (4.5:1) on the resolved dark surface colour, keeping a colour that already does.

### Contrast helpers (`BrandColours`)

- `ContrastRatio` (existing).
- A WCAG rating from a ratio: **AAA** (7:1 and above), **AA** (4.5:1), **AA large text only** (3:1), **Below AA** (under 3:1).
- The ratio a brand colour gets in a mode: the lower of its ratios against that mode's page and card backgrounds.
- The default text colours per mode, for rating backgrounds: the light and dark palettes' normal text colours.

## 2. Colour input and pages

### `BrandColourField`

- A filled square of the chosen colour as the input's start adornment; the palette button stays.
- Under the input, a rating chip: "AAA 8.2:1", "AA 5.1:1", "AA large text only 3.4:1", or "Below AA 2.1:1" in warning amber, followed by "on the page and cards". The field takes the backgrounds to rate against as parameters.
- Below AA adds one line: text in this colour may be hard for some people to read. This replaces the "hard to read on white" alert.
- **Automatic mode** (dark colours): when left automatic, the field shows the worked-out colour and the helper "Automatic, from the light primary" (or secondary); choosing a colour makes it explicit, and **Use automatic** clears it.
- **Default mode** (backgrounds, and Group fields falling back to the MSP): the existing "Using the MSP's (#...)" or "Using the default (#...)" helper and its reset button.
- **Background mode:** a background field rates the mode's normal text colour on it, so the chip reads, for example, "AA 6.0:1 for text".

### Manage > Branding

- **Colours:** two columns, **Light mode** and **Dark mode**, each with Primary and Secondary, stacking at phone width.
- **Advanced: backgrounds:** a collapsed expansion panel with page and card background for light and dark, each with **Use the default**.
- Changing any background re-rates the brand colours straight away.
- The light and dark previews reflect every colour.

### Group branding dialog

The same two-column layout for the four colours, each falling back to the MSP's as now. No background fields. Its preview reflects every colour.

## 3. Where colours are used

### Portal theme (`PortalTheme.For`)

- **Light palette:** `Primary`, `Secondary`, app bar in the secondary as now, `Background` and `Surface` from the light backgrounds, contrast texts as now.
- **Dark palette:** `Primary` and `Secondary` from the dark colours, `Background` and `Surface` from the dark backgrounds, app bar and drawer slightly darker than the dark page background (as `#0F1724` is to `#111827`).
- Text colours are unchanged.

### Portal accents

- Page and section headings in the secondary colour.
- The pass rate trend line in the secondary colour.
- Footer links in the secondary colour.
- Status chips: "Monitoring only" and "No reports yet" in the secondary colour; "Protected" (success) and "Needs attention" (warning) unchanged. Health check chips on a domain's page are unchanged.

### PDF report

Uses the light colours; paper is white, so backgrounds are ignored.

- Primary: the cover band, product name and page title (Heading 1), as now.
- Secondary: section headings (Heading 2), and table header rows filled with the secondary colour and readable text (`PortalTheme.ReadableTextOn`).
- The trend chart's series colours are unchanged.

### Email

- Primary: the top border, as now.
- Secondary: the heading and the summary table's header row.

## 4. Saving, audit, migration, demo, docs

- **Validation:** each new colour is validated with `BrandColours.IsValid` when the branding page and the Group dialog save; blank is stored as null.
- **Audit:** the existing branding and Group branding audit entries include the new fields, showing "automatic" or "default" for null.
- **Migration:** one migration adding the eight nullable columns. Existing installs render as before apart from the new secondary accents.
- **Demo:** the demo seeder's branding reset covers the new fields, and one demo Group gets explicit dark colours.
- **Docs:** the Client portal page's branding section covers light and dark colours, automatic dark colours, backgrounds, and what AA and AAA mean; branding screenshots are refreshed. Release notes in the 0.9.2 post.

## 5. Testing

- **Contrast:** ratings at the 3:1, 4.5:1 and 7:1 boundaries; a brand colour's ratio is the lower of page and card.
- **Resolution:** each step of the dark-colour order, including a Group light colour never pairing with the MSP dark colour; automatic colours meet 4.5:1 on the configured dark surface; backgrounds fall back to the defaults.
- **Theme:** palette slots per mode; the dark app bar follows the dark background; status chips keep success and warning.
- **Saving:** invalid hex refused, blank stored as null, audit details for the new fields, on both the MSP and Group saves.
- **Reports:** Heading 2 and table header rows in the secondary colour; the email's heading and header row in it.
- **Browser check** (playwright-edge): branding page and Group dialog, previews in light and dark, the Advanced section, ratings updating on change, and phone width.
