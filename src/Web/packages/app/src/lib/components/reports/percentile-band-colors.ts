/** Percentile band colours shared by every chart of a series, so one series reads the same everywhere. */
export interface BandColors {
  outer: string;
  inner: string;
  median: string;
}

export const GLUCOSE_BANDS: BandColors = {
  outer: "oklch(from var(--chart-1) l c h / 0.35)",
  inner: "oklch(from var(--chart-1) l c h / 0.6)",
  median: "var(--chart-1)",
};

export const IOB_BANDS: BandColors = {
  outer: "color-mix(in oklab, var(--insulin) 22%, transparent)",
  inner: "color-mix(in oklab, var(--insulin) 45%, transparent)",
  median: "var(--insulin)",
};

export const COB_BANDS: BandColors = {
  outer: "color-mix(in oklab, var(--carbs) 22%, transparent)",
  inner: "color-mix(in oklab, var(--carbs) 45%, transparent)",
  median: "var(--carbs)",
};

/** Tints of the one basal hue; texture tells them apart in print. */
export const BASAL_BANDS: BandColors = {
  outer: "color-mix(in oklab, var(--basal) 45%, var(--background))",
  inner: "var(--basal)",
  median: "var(--entry-basal)",
};
