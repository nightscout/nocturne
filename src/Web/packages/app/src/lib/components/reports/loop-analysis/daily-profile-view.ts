import type {
  DailyProfileDay,
  DailyProfileDayCoverage,
  DailyProfileRange,
  LoopAnalysisAggregate,
} from "$lib/api";
import { Key } from "@nocturne/ui/keys";

export const SLOTS_PER_DAY = 288;
export const SLOT_MINUTES = 5;

/** Reads one slot of a coverage bitmap, laid out as `DailyProfileDayCoverage` documents. */
export function covers(base64: string | undefined | null, slot: number): boolean {
  if (!base64) return false;
  const byte = atob(base64).charCodeAt(slot >> 3) || 0;
  return (byte & (1 << (slot & 7))) !== 0;
}

export type CoverageSeries = Exclude<keyof DailyProfileDayCoverage, "analysed">;

export const COVERAGE_SERIES: CoverageSeries[] = [
  "glucose",
  "scheduledBasal",
  "deliveredBasal",
  "netLoopCorrection",
  "iobCob",
];

export interface ContributingDay {
  day: DailyProfileDay;
  series: CoverageSeries[];
}

/**
 * The days that went into a slot. `analysed` decides where the response carries it; where it is
 * withheld, a day counts when any series it does carry covered the slot.
 */
export function contributingDays(range: DailyProfileRange | undefined, slot: number): ContributingDay[] {
  const result: ContributingDay[] = [];
  for (const day of range?.days ?? []) {
    const coverage = day.coverage ?? {};
    const series = COVERAGE_SERIES.filter((s) => covers(coverage[s], slot));
    const analysed = coverage.analysed != null ? covers(coverage.analysed, slot) : series.length > 0;
    if (analysed) result.push({ day, series });
  }
  return result;
}

export interface SlotPoint<T> {
  slot: number;
  value: T;
}

export interface SlotRun<T> {
  sparse: boolean;
  points: SlotPoint<T>[];
}

export type AggregatePoint = SlotPoint<LoopAnalysisAggregate>;

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

export function aggregateRuns<S extends object>(
  slots: S[],
  pick: (slot: S) => LoopAnalysisAggregate | undefined
): SlotRun<LoopAnalysisAggregate>[] {
  return slotRuns(slots, pick, (value) => value.sparse ?? false);
}

export type SlotKeyAction =
  | { kind: "step"; by: number }
  | { kind: "to"; slot: number }
  | { kind: "panel"; by: number }
  | { kind: "select" }
  | { kind: "close" };

/**
 * What a key does on a slotted panel: move along the axis (an hour at a time with Shift, or to
 * either end), move to the panel above or below, pick the slot, or close the list it opened.
 */
export function slotKeyAction(event: KeyboardEvent, lastSlot = SLOTS_PER_DAY - 1): SlotKeyAction | null {
  const step = event.shiftKey ? 12 : 1;
  switch (event.key) {
    case Key.ArrowRight:
      return { kind: "step", by: step };
    case Key.ArrowLeft:
      return { kind: "step", by: -step };
    case Key.Home:
      return { kind: "to", slot: 0 };
    case Key.End:
      return { kind: "to", slot: lastSlot };
    case Key.ArrowDown:
      return { kind: "panel", by: 1 };
    case Key.ArrowUp:
      return { kind: "panel", by: -1 };
    case Key.Enter:
    case Key.Space:
      return { kind: "select" };
    case Key.Escape:
      return { kind: "close" };
    default:
      return null;
  }
}

/** Whether a key closes the day list. */
export function isEscape(event: KeyboardEvent): boolean {
  return event.key === Key.Escape;
}

/** `HH:mm` for a minute of the local day, as Day in Review's `time` parameter reads it. */
export function clockTime(minuteOfDay: number): string {
  const h = Math.floor(minuteOfDay / 60) % 24;
  const m = minuteOfDay % 60;
  return `${String(h).padStart(2, "0")}:${String(m).padStart(2, "0")}`;
}

/** A rounded-up axis end, so the tallest mark never touches the top. */
export function axisEnd(values: (number | null | undefined)[], floor: number): number {
  const max = Math.max(floor, ...values.filter((v): v is number => v != null && Number.isFinite(v)));
  const step = max <= 1 ? 0.25 : max <= 5 ? 1 : max <= 20 ? 5 : max <= 100 ? 10 : 50;
  return Math.ceil((max * 1.05) / step) * step;
}

/** A wall-clock instant on an arbitrary date, for formatting a slot's time with the viewer's clock format. */
export function atMinute(minuteOfDay: number): Date {
  return new Date(2000, 0, 1, 0, minuteOfDay);
}

/** Two decimals, with a value that rounds to zero read as 0.00 rather than -0.00. */
export function twoPlaces(v: number): string {
  return (Math.round(v * 100) === 0 ? 0 : v).toFixed(2);
}

/** Two decimals with a sign: + above zero, - below, none at a value that rounds to zero. */
export function signedTwoPlaces(v: number): string {
  return `${Math.round(v * 100) > 0 ? "+" : ""}${twoPlaces(v)}`;
}
