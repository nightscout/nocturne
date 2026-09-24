---
name: Nocturne
description: A self-hosted, Nightscout-compatible diabetes data platform that shows the data and leaves the conclusions to the user.
colors:
  foreground: "oklch(0.13 0.028 261.692)"
  primary: "oklch(0.21 0.034 264.665)"
  background: "oklch(1 0 0)"
  primary-foreground: "oklch(0.985 0.002 247.839)"
  muted: "oklch(0.967 0.003 264.542)"
  border: "oklch(0.928 0.006 264.531)"
  muted-foreground: "oklch(0.551 0.027 264.364)"
  muted-dark: "oklch(0.278 0.033 256.848)"
  destructive: "oklch(0.577 0.245 27.325)"
  success: "oklch(0.47 0.13 149.214)"
  warning: "oklch(0.49 0.117 58.318)"
  info: "oklch(0.546 0.245 262.881)"
  glucose-in-range: "oklch(0.6 0.118 184.704)"
  glucose-tight-range: "oklch(0.72 0.16 150)"
  glucose-low: "oklch(0.646 0.222 41.116)"
  glucose-very-low: "oklch(0.577 0.245 27.325)"
  glucose-high: "oklch(0.65 0.18 270)"
  glucose-very-high: "oklch(0.55 0.25 15)"
  insulin: "rgb(30, 150, 252)"
  basal: "oklch(0.75 0.134 248.6)"
  carbs: "oklch(0.769 0.188 70.08)"
  demo: "oklch(0.558 0.288 302.321)"
  brand: "oklch(0.6 0.118 184.704)"
  sunken: "oklch(0.1 0.025 261)"
typography:
  display:
    fontFamily: "Cabin, sans-serif"
    fontSize: "clamp(2.5rem, 7vw, 4.8rem)"
    fontWeight: 700
    lineHeight: 1.06
    letterSpacing: "-0.025em"
  headline:
    fontFamily: "Cabin, sans-serif"
    fontSize: "clamp(1.6rem, 3.5vw, 2.5rem)"
    fontWeight: 700
    lineHeight: 1.2
    letterSpacing: "-0.02em"
  title:
    fontFamily: "Cabin, sans-serif"
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: 1
  body:
    fontFamily: "Cabin, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: 1.43
  label:
    fontFamily: "Cabin, sans-serif"
    fontSize: "0.75rem"
    fontWeight: 500
    lineHeight: 1.33
  eyebrow:
    fontFamily: "Montserrat, sans-serif"
    fontSize: "0.75rem"
    fontWeight: 700
    letterSpacing: "0.14em"
  micro:
    fontFamily: "Cabin, sans-serif"
    fontSize: "0.625rem"
    lineHeight: 1.5
rounded:
  sm: "6px"
  md: "8px"
  lg: "10px"
  xl: "14px"
  full: "9999px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "24px"
  xl: "32px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.primary-foreground}"
    rounded: "{rounded.md}"
    padding: "8px 16px"
    height: "36px"
  button-outline:
    backgroundColor: "{colors.background}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.md}"
    padding: "8px 16px"
    height: "36px"
  button-ghost:
    textColor: "{colors.foreground}"
    rounded: "{rounded.md}"
    padding: "8px 16px"
    height: "36px"
  button-alarm:
    backgroundColor: "{colors.destructive}"
    textColor: "{colors.primary-foreground}"
    rounded: "{rounded.md}"
    padding: "0 32px"
    height: "56px"
  card:
    backgroundColor: "{colors.background}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.xl}"
    padding: "24px"
  card-stat:
    backgroundColor: "{colors.background}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.xl}"
    padding: "16px"
  input:
    backgroundColor: "{colors.background}"
    textColor: "{colors.foreground}"
    rounded: "{rounded.md}"
    padding: "0 12px"
    height: "36px"
  badge-success:
    textColor: "{colors.success}"
    typography: "{typography.label}"
    rounded: "{rounded.md}"
    padding: "2px 8px"
---

# Design System: Nocturne

This system covers both the tenant app (`packages/app`) and the marketing and docs portal (`packages/portal`). Both import `@nocturne/ui/theme.css`, so they share one set of tokens and one component library, `packages/ui`. The portal adds its own layer on top, described under each section's **Portal** notes.

## Overview

**Creative North Star: "The Clinical Ledger"**

Nocturne is a precise, dense, honest record of a body's data. It reads like a well-kept ledger: tabular, exact, and indifferent to fashion. The user comes to read numbers and trace lines, so the interface is a quiet grid of cards on a neutral cool-navy field. Colour is spent almost entirely on meaning. Glucose range, insulin, carbs, basal, severity, and record kind each have a named token, and nothing borrows a clinical hue for decoration.

The screens are dense on purpose. A dashboard carries a strip of device pills, stat cards, and a multi-lane glucose chart in one viewport, because the people using it read these values daily and want them together. Density is held in check by consistent card rhythm (24px inside, 24px between), hairline borders, and a type scale that rarely goes above a card title. The loudest thing on any screen should be the current reading.

The system is themeable at runtime. Nocturne is the default, and Trio, AAPS, and Classic theme packs (`packages/ui/src/styles/*-theme.css`) re-point the same tokens to match the apps users already know. Consumers therefore reference tokens and never literal values. It is also print-aware: reports print with their clinical colours intact and shadows stripped.

**Key Characteristics:**
- A neutral cool-navy base (hue ≈ 261–265) with near-zero chroma, in light and dark
- Every clinical concept has a named token, and colour is reserved for meaning
- Flat, tonal surfaces with hairline borders, and shadows that barely register
- Dense, card-based layouts led by one dominant reading
- Runtime theme packs, so tokens are always referenced, never hard-coded
- Reports print faithfully

## Colors

The base is a cool, almost colourless slate. On top of it sits a large, strictly role-bound palette of clinical and data colours, all specified in OKLCH.

### Primary
- **`--primary`**: the primary action fill and the default button and badge. In light mode it is dark slate. In dark mode it flips to near-white (`oklch(0.928 0.006 264.531)`), so the primary action is always the highest-contrast element rather than a coloured one. The app has no brand accent.

### Secondary
- **`--glucose-in-range`** (shared with `--status-normal`): a reading within its target range, and the normal state for pump modes and system events. It is the colour that appears most often, but it stays a clinical token. Theme packs swap it: Trio makes it LoopGreen.

### Tertiary: clinical and data tokens
Defined in `packages/ui/src/styles/nocturne-theme.css`. Each token family has exactly one job.
- **Glucose range:** `--glucose-very-low`, `--glucose-low`, `--glucose-in-range`, `--glucose-tight-range`, `--glucose-high`, and `--glucose-very-high`. `--glucose-high` is violet, not orange, so low and high are never confused.
- **Therapy:** `--insulin` (bolus and IOB), `--basal` (scheduled and temp basal are told apart by lightness, not hue), and `--carbs`.
- **Record-kind text grade:** `--entry-bolus`, `--entry-basal`, `--entry-carbs`, `--entry-bg-check`, and `--entry-device-event` are the same hues as the chart tokens, held at 4.5:1 on the page and on their own 10–20% tints. Use them for text, icons, and chip labels. The chart tokens are for fills and strokes only.
- **Severity:** `--severity-urgent`, `--severity-hazard`, `--severity-warn`, and `--severity-info` cover notification and tracker urgency, as text and tints. Each scheme sets its own lightness. `--severity-info` is blue, never green.
- **System events and status:** `--system-event-*` and `--status-*` (critical, warning, normal, info).
- **Pump modes, predictions, GRI zones, eHbA1c bands, heatmap, sleep lanes, weekdays:** each family is its own set: `--pump-mode-*`, `--pred-*`, `--gri-zone-*`, `--ehba1c-*`, `--glucose-heatmap-*`, `[data-lane]`, and `--weekday-*`. The weekday series is derived from Okabe-Ito so it stays distinguishable for colour-blind viewers.
- **`--demo`**: synthetic demo data only, kept apart from every clinical and outcome hue.
- **`--report-overview`, `--report-patterns`, `--report-lifestyle`, `--report-treatment`**: identity only, on the reports hub and in each report's header.

### Neutral
- **`--background`**: the page. In light mode it is white, and `--card` has the same value, so the border separates them. In dark mode it is deep navy.
- **`--card`, `--popover`**: in dark mode these sit one tonal step above `--background`.
- **`--secondary`, `--muted`, `--accent`**: hover fills, muted panels, and secondary buttons. They are one step above white in light mode and one step above `--card` in dark mode (`muted-dark` in the frontmatter).
- **`--foreground`**: body text.
- **`--muted-foreground`**: secondary text, captions, units, and "1 min ago" timestamps.
- **`--border`, `--input`**: 1px dividers and field strokes. In dark mode they are 10% and 15% white.
- **`--primary-foreground`**: text on primary fills.
- **`--sidebar-*`**: the sidebar's own set. In light mode the sidebar is off-white.

### Outcome colours
`--success`, `--warning`, `--info`, and `--destructive` describe **UI outcomes**: saved, failed, stale, syncing, remove. In light mode they are held at 4.5:1 on their own 5–20% tints, because they are mostly read as tinted text. The opaque `--color-warning-subtle` and `--color-info-subtle` mixes (mixed in oklab) back surfaces that content scrolls beneath, such as sticky banners.

### Portal
The portal renders dark only (`app.html` pins `.dark`), so the `brand` and `sunken` values in the frontmatter are their dark values.
- **`--brand`**: the portal's accent. It is a separate token from `--glucose-in-range` because theme packs can repoint that one.
- **`--sunken`**: recessed terminals and demo canvases.
- **`--feature-*`**: the identity accent of each headline feature, used only for its check marks and the demos.
- **`--highlight`**: carries a per-item accent.

### Named Rules
**The One Meaning Rule.** A colour token has exactly one meaning. Glucose-range, outcome, severity, record-kind, and identity colours are never interchangeable, even where two share a value. `--status-warning` is not `--warning`.

**The Colour-Plus-One Rule.** No clinical state is carried by colour alone. Pair every range, severity, or mode colour with a second channel: a number, label, icon, position on an axis, dash pattern, or chart texture (`CHART_TEXTURES`).

**The Referenced-Not-Literal Rule.** Components reference tokens (`bg-glucose-low`, `var(--insulin)`) and never Tailwind palette literals such as `bg-green-500`. Theme packs and print overrides only work through tokens. The one sanctioned exception is `STATUS_TOKENS` in `packages/ui/src/lib/tokens.ts`, which exists for out-of-browser consumers (chat cards, email) and has a guard test against drift.

## Typography

**Body Font:** Cabin (variable, 400–700), with sans-serif fallback
**Brand Font:** Montserrat (variable, 100–900), with sans-serif fallback

**Character:** Cabin is a humanist sans with open apertures. It stays legible at 10–14px in dense tables and chart labels, and its figures hold up in glucose values. Montserrat is the brand voice: the wordmark, uppercase eyebrows, and the onboarding's hairline-weight (250) display headings. It never sets running text.

### Hierarchy
- **Display** (700, `clamp(2.5rem, 7vw, 4.8rem)`, 1.06, −0.025em): portal hero only (`--text-display`).
- **Headline** (700, `clamp(1.6rem, 3.5vw, 2.5rem)`, 1.2, −0.02em): portal section headings (`--text-section`). The portal also has `--text-headline` and `--text-subsection` steps.
- **Reading** (700, 36px, `text-4xl`, in the `lg` tile): the current glucose value and the largest type in a standard app view. Clock faces scale it further.
- **Title** (600, 16px, leading-none): card titles such as "Time in Range" and "Blood Glucose".
- **Body** (400, 14px, 1.43): the UI default (`text-sm`). Inputs are 16px on mobile to prevent iOS zoom and 14px from `md` up.
- **Label** (500, 12px): badges, captions, and units.
- **Eyebrow** (Montserrat 700, 12px, 0.14em, uppercase): portal section kickers, for example "01 · Why this exists".
- **Micro** (10px, `text-2xs`): the floor for UI text, used for dense counts and table-header tags.
- **Chart annotation** (8px / 7px, `text-3xs` / `text-4xs`): only inside a chart lane or beside a marker glyph, never outside a chart.

### Named Rules
**The Ten-Pixel Floor Rule.** No UI text is set below 10px (`text-2xs`). The 3xs and 4xs steps exist only for annotations inside a chart.

**The Unit-Beside-Value Rule.** A glucose, insulin, or carb value is never set without its unit (mg/dL or mmol/L, U, g). The unit takes the label or muted grade so the figure leads.

## Layout

The app is a collapsible left sidebar (`--sidebar`) beside a scrolling main column. The sidebar carries the wordmark, a compact current-reading tile with sparkline, the navigation, and the account row. The dashboard opens with a horizontal strip of device and therapy pills (COB, basal, IOB, reservoir, site and sensor age, clock), then a row of stat cards, then the full-width glucose chart with its basal, mode, and IOB/COB lanes.

Spacing runs on Tailwind's 4px grid. Cards use 24px internal padding and a 24px gap between header and content. Stat tiles use 16px. Grids of cards separate with 16–24px gutters. Portal sections sit in a centred 1200px container with 24px side padding and 80px vertical rhythm, divided by top hairlines. Feature grids use `repeat(auto-fit, minmax(240px, 1fr))` with 1px gaps over a border-coloured backing, so the gaps read as ruled lines.

Reports have a print layout. App chrome, banners, and toasts are hidden; `main` overflow is released; sticky and fixed elements go static; cards, table rows, figures, and SVGs avoid page breaks; tables repeat their headers; and `print-color-adjust: exact` keeps the clinical colours.

Fullscreen surfaces (clock faces, alarms) own their background. Controls over them use the `overlay` button and badge variants, which are white at 80% on a 10% white hover.

## Elevation & Depth

Nocturne is flat and tonal. In light mode, depth comes from 1px borders, with `--card` the same value as `--background`. In dark mode, cards and popovers step one tonal level lighter than the page (`--card` above `--background`), and muted or secondary fills step once more. Shadows are vestigial: `shadow-xs` on buttons and inputs, `shadow-sm` on cards. Print removes them completely. The only real lift is `shadow-lg` on the extra-large alarm button, which has to read as a physical control.

### Shadow Vocabulary
- **Control** (`shadow-xs`): buttons (except ghost, link, and menu variants) and inputs. It is barely perceptible and suggests pressability.
- **Surface** (`shadow-sm`): cards at rest.
- **Alarm** (`shadow-lg`): only on `size="xl"` full-screen alarm and emergency actions.

### Named Rules
**The Flat-By-Default Rule.** Surfaces separate by tone and hairline, not shadow. Adding a shadow step beyond `shadow-sm` to a resting surface is a system change, not a component choice.

## Shapes

The corners are gently rounded from one root radius, `--radius: 0.625rem` (10px). The derived steps are sm 6px (menu rows), md 8px (buttons, inputs, badges), lg 10px, and xl 14px (cards). Pills and remove pips are fully round. Borders are 1px hairlines everywhere. Dashed borders carry meaning: an empty state (`card variant="dashed"`), an add-item placeholder (`button variant="dashed"`), or a disconnected live reading. Portal feature grids clip a 1px-gap grid inside a 12px rounded frame.

## Components

Components come from shadcn-svelte, extended in `packages/ui` with Tailwind Variants (`tv()`). Every variant exists for a named use case, recorded in a comment beside it. Add a use-case variant rather than restyling at the call site.

### Buttons
- **Shape:** gently rounded (8px). The default height is 36px, with sm 32px, xs 28px, and lg 40px.
- **Primary:** `--primary` fill with `--primary-foreground` text, 16px horizontal padding, 14px medium weight, and a 90% fill on hover.
- **Focus:** the border shifts to `--ring` and a 3px `--ring` ring appears at 50%. Invalid states swap in `--destructive`.
- **Variants:**
  - outline (`--background` with a `--border` stroke; `--input` at 30% in dark mode);
  - secondary, ghost, ghost-muted, subtle, and link;
  - destructive, ghost-destructive, and outline-destructive for removes;
  - combobox and menu;
  - dashed (add item);
  - overlay (fullscreen);
  - brand (colour supplied by an admin through `--button-bg` / `--button-fg`).
- **Sizes of note:**
  - `cta` (40px, 16px type) for marketing calls to action;
  - `xl` (56px, 18px semibold, `shadow-lg`) for full-screen alarms, read at a glance;
  - `inline` and `inline-xs` for link-buttons in running text;
  - `reveal` hides a button until its group is hovered or focused, but keyboard focus always shows it.

### Chips / Badges
- **Style:** 8px radius, 2px × 8px padding, 12px medium weight. The `sm` size (10px) is for dense rows and the `lg` size (14px) is for a status read across a room.
- **Outcome variants** (success, warning, info, demo): a 15% tint of the tone with text in that tone and no border.
- **Record-kind variants** (`entry-*`): a 10% tint, text in the kind's text grade, and a 30% border.
- **Severity variants:** solid fills with their paired foreground, for counts and level chips.
- **Cluster variants:** 15–20% tints for sensor-integrity confidence.
- **Live:** `animate-pulse` for something running now.

### Cards / Containers
- **Corner Style:** 14px (`rounded-xl`).
- **Background:** `--card`, the same tone as the page in light mode and one step up in dark mode.
- **Shadow Strategy:** `shadow-sm` (see Elevation & Depth).
- **Border:** a 1px hairline.
- **Internal Padding:** 24px vertical padding with a 24px gap, and 24px horizontal padding on each slot. `size="sm"` stat tiles use 16px all round.
- **Variants:**
  - tinted outcome cards (destructive, success, warning, info: a 30–50% border over a 5% fill);
  - primary (report headline figures);
  - muted (supporting notes, filter bars);
  - dashed (empty state);
  - `interactive` (a card that is a link; hover fills with accent at 50%).

### Inputs / Fields
- **Style:** a hairline `--input` stroke, 8px radius, 36px height, 12px padding, and `shadow-xs`. In dark mode the fill is a 30% input tint.
- **Focus:** `--ring` border with a 3px ring at 50%, the same as buttons.
- **Error / Valid / Disabled:** `aria-invalid` switches to a destructive border and ring. `data-valid` (a value the server accepted) shows a 60% success border. Disabled fields are at 50% opacity.
- **Variants:** `code` (centred, uppercase, wide-tracked, for pairing codes) and `title` (borderless in-place rename that shows its border on focus).

### Navigation
The sidebar navigation is a list of rows, each a 16px Lucide icon with 14px text, 8px padding, and 8px rounding. The hovered or active row takes a `--sidebar-accent` fill, and the active row is set in medium weight. Groups expand with a chevron. The sidebar collapses to icons, and the current-reading tile collapses with it (`xs` size). On mobile the sidebar becomes a sheet behind a trigger.

### Banner
A sticky, full-width strip for app-wide conditions such as a stale connection or maintenance. It uses 14px type, an 8px × 16px pad, a bottom hairline in the tone at 30%, and an opaque `*-subtle` fill so that scrolling content never shows through. It comes in warning and info variants.

### Glucose Value Indicator (signature)
The current reading is a solid tile filled with the range colour, holding the value in heavy numerals, with a trend arrow, the delta, and the age of the reading beside it. It comes in `lg` (dashboard and top bar), `sm` (sidebar), and `xs` (collapsed sidebar). It pulses once when the value changes. When the reading is stale, the fill drops to `--muted`. When the connection is lost, the border turns dashed, and stale plus disconnected flashes the border. The value is never shown without its age.

### Charts
Charts are drawn with layerchart. The main glucose chart stacks lanes over a shared time axis: basal (scheduled pale, temp full, with a hatched pattern for gaps), pump mode, glucose points coloured by range against target bands, predictions (a cone or per-input lines), and IOB/COB. A vertical rule marks "now" and the last pump sync. Chart fills and strokes use the chart tokens, and labels use the `entry-*` text grades. Textures from `CHART_TEXTURES` give print and colour-blind readers a second channel.

## Do's and Don'ts

### Do:
- **Do** reference every colour through its token (`bg-glucose-low`, `text-entry-carbs`, `var(--insulin)`) so the Trio, AAPS, and Classic packs and print overrides apply.
- **Do** pair every clinical colour with a second channel (value, label, icon, position, dash, or texture), per the Colour-Plus-One Rule.
- **Do** use the `entry-*` text grades for any text or icon in a record kind's colour, and the chart tokens only for fills and strokes.
- **Do** show every glucose value with its unit and age, and drop a stale reading to the muted treatment rather than leaving it in its range colour.
- **Do** add a named `tv()` variant with a one-line use-case comment when a component needs a new look, instead of overriding classes at the call site.
- **Do** keep printed reports faithful: clinical colours at `print-color-adjust: exact`, no shadows, cards unbroken across pages.
- **Do** use the `overlay` variants over fullscreen clock and alarm surfaces whose background the theme does not own.

### Don't:
- **Don't** use Tailwind palette literals (`bg-green-500`, `text-orange-500`) for any clinical or outcome state.
- **Don't** use `--success` or `--warning` for glucose or therapy state. They are UI outcomes. Use the `--glucose-*`, `--status-*`, or `--severity-*` families.
- **Don't** use `--glucose-in-range` as a brand or decorative accent. In the portal, use `--brand`.
- **Don't** use `--demo` for anything except synthetic demo data.
- **Don't** set UI text below 10px. `text-3xs` and `text-4xs` are for chart annotations only.
- **Don't** raise a resting surface above `shadow-sm`. `shadow-lg` belongs only to the `xl` alarm button.
- **Don't** use emoji. Icons are Lucide.
