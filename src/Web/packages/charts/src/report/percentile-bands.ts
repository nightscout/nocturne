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

/** One hue per cohort, in cohort order, so a cohort reads the same in every panel, the table and the list. */
const COHORT_HUES = ["var(--chart-1)", "var(--chart-3)", "var(--chart-2)"];

export function cohortBands(index: number): BandColors {
  const hue = COHORT_HUES[index % COHORT_HUES.length];
  return {
    outer: `oklch(from ${hue} l c h / 0.2)`,
    inner: `oklch(from ${hue} l c h / 0.4)`,
    median: hue,
  };
}

/** One slot's spread of a series, as the API's Loop Analysis aggregates carry it. */
export interface PercentileAggregate {
  p10?: number;
  p25?: number;
  p50?: number;
  p75?: number;
  p90?: number;
  /** Fewer values than the threshold below which the spread is not to be read as a pattern. */
  sparse?: boolean;
}

export interface SlotPoint<T> {
  slot: number;
  value: T;
}

export interface SlotRun<T> {
  sparse: boolean;
  points: SlotPoint<T>[];
}

export type AggregatePoint = SlotPoint<PercentileAggregate>;

/**
 * Consecutive slots holding the series, split where it is absent (a gap, never drawn as zero) and
 * where its sparse flag changes. A run ending at a sparseness change repeats the next run's first
 * point so the two meet instead of leaving a sliver. Slots are numbered by their place in the
 * list, which every series lays out whole, slot 0 first.
 */
export function slotRuns<S extends object, T>(
  slots: S[],
  pick: (slot: S) => T | null | undefined,
  isSparse: (value: T, slot: S) => boolean
): SlotRun<T>[] {
  const runs: SlotRun<T>[] = [];
  let current: SlotRun<T> | null = null;
  for (const [position, slot] of slots.entries()) {
    const value = pick(slot);
    if (value == null) {
      current = null;
      continue;
    }
    const sparse = isSparse(value, slot);
    const point = { slot: position, value };
    if (current && current.sparse === sparse) {
      current.points.push(point);
      continue;
    }
    current?.points.push(point);
    current = { sparse, points: [point] };
    runs.push(current);
  }
  return runs;
}

export function aggregateRuns<S extends object, A extends PercentileAggregate>(
  slots: S[],
  pick: (slot: S) => A | null | undefined
): SlotRun<A>[] {
  return slotRuns(slots, pick, (value) => value.sparse ?? false);
}
