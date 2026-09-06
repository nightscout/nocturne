# Nocturne taskbar glucose sparkline

A Windhawk mod that renders a glucose sparkline, plus a predicted-glucose
continuation, loop status (IOB/COB) and server alerts, on the Windows 11
taskbar. The sparkline is auto-positioned to track the centered app icons and
shifts as icons are added or removed.

The mod does not poll any network or hold any credentials. It reads a local file
that a Nocturne client writes each poll — either the desktop companion (default
`%LOCALAPPDATA%\Nocturne\glucose.json`) or the packaged Windows 11 widget (which
writes the same logical path; Windows may redirect that write into the widget's
package container). The mod reads every candidate path and renders whichever
source is freshest, so it keeps working if only one of them is running. The mod
only ever reads these files.

## Requirements

- Windows 11.
- [Windhawk](https://windhawk.net/) installed.
- At least one Nocturne client running and writing the summary file — the
  desktop companion and/or the packaged Windows 11 widget (see
  [The data file](#the-data-file) below). Without one the mod has nothing to
  render.

## Install

1. Open Windhawk.
2. **Create New Mod**.
3. Paste the contents of [`mod.wh.cpp`](mod.wh.cpp) into the editor.
4. **Compile** (`Ctrl+B`).
5. **Enable** the mod.

Windhawk compiles from its editor buffer, not from the file on disk: paste the
current `mod.wh.cpp` each time it changes and recompile.

## Settings

The mod exposes these settings in Windhawk (open the mod → **Settings**):

### Data

- **Summary JSON path** — set this to force a single source. Blank (default) auto-
  discovers two sources: the companion's `%LOCALAPPDATA%\Nocturne\glucose.json`
  and the packaged widget's redirected file, and renders whichever is freshest.
  Supports `%ENV%` and `~`.
- **Poll interval (seconds)** — how often the mod re-reads the file.
- **Stale after (seconds)** — when the latest reading (`current.mills`) is older
  than this, the card dims.
- **Display unit** — `mmol/L` or `mg/dL`. The summary's glucose values are always
  canonical mg/dL; the mod converts to this unit for display (mmol/L rounds to
  1 dp, mg/dL to a whole number).
- **Target range low / high** — the target-range band and in-range colouring,
  expressed in the display unit (the summary has no range of its own).
  Defaults `3.9` / `10.0`.

### Style

- **In-range / High / Low color** — line and value colour by state, using the
  target range above.
- **Predicted line color** — color of the dashed forecast continuation.
- **Show target-range band / current value / trend arrow** — element toggles.
- **Show iob status (IOB/COB)** — show an `IOB 1.2U · COB 14g` line under the
  value, from the summary's `iob`/`cob`. Hidden when both are zero or absent.
- **Sparkline width / height**, **Line thickness**, **Font size** — geometry.
- **Auto text color** / **Text color** — follow the taskbar light/dark theme, or
  use a fixed colour.

### Alert

- **Pulse a border on an active alarm** — when the summary reports an active,
  un-silenced server alarm the whole card pulses a coloured outline (low alarms
  use the Low colour, everything else the High colour). If the summary carries no
  `alarm` field at all, this falls back to a local range comparison.
- **Alert border thickness**.

### Tile order

- **Tile order (`rank`)** — left-to-right order among taskbar widgets sharing the
  `TaskbarWidgetHost` (lower = further left).

## The data file

- **Shape:** the raw `V4SummaryResponse` returned by the Nocturne
  `GET /api/v4/summary` endpoint. The authoritative schema is the V4 OpenAPI
  document (`V4SummaryResponse` / `V4GlucoseReading` / `V4AlarmState` /
  `V4Predictions`); there is no bespoke contract maintained here.
- **Sample:** [`samples/summary.sample.json`](samples/summary.sample.json) — a
  post-meal curve in mg/dL, shaped like `V4SummaryResponse` for local dev.
- **Path:** `%LOCALAPPDATA%\Nocturne\glucose.json` for the companion. The packaged
  widget writes the same logical path; depending on the OS/packaging, Windows may
  redirect that AppData write into its package container
  (`%LOCALAPPDATA%\Packages\Nightscout.Nocturne.Widget_*\LocalCache\…`), so the mod
  globs that location too. If the write is not redirected, the widget simply shares
  the companion's path. Both are overridable by setting an explicit path in
  settings (which then becomes the sole source).

Notes on how the mod reads the shape:

- `sgv`, `delta` and `predictions.values` are **mg/dL** (canonical); the mod
  converts them to the display unit.
- Timestamps (`current.mills`, `history[].mills`, `serverMills`,
  `predictions.startMills`) are epoch milliseconds (integers), not ISO strings.
- `current`, `predictions` and `alarm` may be `null`/absent; `history` may be
  empty; `predictions.values` may be empty (predictions disabled → no dashed
  line). `history` order is not guaranteed — the mod sorts by `mills` ascending.
- Predicted points are reconstructed from `predictions`: point *i* sits at
  `startMills + i * intervalMills` with value `values[i]`.
- The IOB/COB line comes from `iob` (units) and `cob` (grams).
- The alert pulse is driven by `alarm` (active when `level > 0` and not
  `isSilenced`).

## Note on data flow

Two Nocturne clients can write the summary file, and the mod reads from either: the desktop companion (a Tauri/Rust app) writes it via the `nocturne-rs` V4 SDK, and the packaged Windows 11 widget writes the same `GET /api/v4/summary` response verbatim from its own poll loop. Each persists the response untouched. This mod is read-only with respect to those files: it never writes, fetches, or authenticates, and it renders whichever source is freshest. If no file is present, or all are malformed or stale, the mod degrades gracefully rather than disturbing the taskbar.
