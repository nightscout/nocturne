/**
 * Shared sleep-stage taxonomy for the sleep trends page (actogram, composition
 * chart) and the single-night report (hypnogram, stage breakdown).
 *
 * The raw SleepStageType enum has more values than we display; laneForStage
 * collapses it to a display lane. Consumers emit the lane as a `data-lane`
 * attribute and let CSS resolve the swatch (see the [data-lane] rules in
 * @nocturne/ui theme.css) — no colour mapping lives here.
 */

/** Classic hypnogram order, top to bottom. Unspecified is appended only when present. */
export const HYPNOGRAM_LANE_ORDER = ["awake", "rem", "light", "deep"];

export const HYPNOGRAM_LANE_LABELS: Record<string, string> = {
  awake: "Awake",
  rem: "REM",
  light: "Light",
  deep: "Deep",
  unspecified: "Unspecified",
};

/**
 * Maps a raw SleepStageType value (or an actogram span state) to its display
 * lane. Matching is exact on the lowercased value; every SleepStageType member
 * is covered and anything unrecognised falls through to Unspecified.
 */
export function laneForStage(stage: string | undefined): string {
  const lower = (stage ?? "").toLowerCase();
  if (lower === "deep") return "deep";
  if (lower === "rem") return "rem";
  if (lower === "light") return "light";
  if (lower === "awake" || lower === "awakeinbed" || lower === "restless" || lower === "outofbed") {
    return "awake";
  }
  // Asleep / Unknown / Unmeasurable / InBed — undifferentiated
  return "unspecified";
}

/** Composition segments keyed to SleepNightSummary minute fields, in stacked order. */
export const SLEEP_COMPOSITION_SEGMENTS = [
  { key: "deepMinutes", label: "Deep", lane: "deep" },
  { key: "remMinutes", label: "REM", lane: "rem" },
  { key: "lightMinutes", label: "Light", lane: "light" },
  { key: "unspecifiedMinutes", label: "Unspecified", lane: "unspecified" },
] as const;
