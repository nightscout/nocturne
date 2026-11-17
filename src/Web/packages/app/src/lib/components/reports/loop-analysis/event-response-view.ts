import type { EventMetrics, EventMetricsSummary, EventResponseEvent } from "#lib/api/index.js";
import type { BandColors } from "#lib/components/reports/percentile-band-colors.js";
import { covers } from "./daily-profile-view";

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

export type EventMetricKey = keyof EventMetrics & keyof EventMetricsSummary;

/** How a figure is shown: as glucose in the viewer's units, minutes, insulin units, or a ratio per unit. */
export type MetricKind = "glucose" | "glucoseChange" | "glucoseDrop" | "minutes" | "units" | "gramsPerUnit" | "glucosePerUnit";

/** The slots where the relative axis is ticked: each whole hour from the event. */
export function hourTicks(minutes: readonly number[]): number[] {
  const ticks = minutes.flatMap((minute, slot) => (minute % 60 === 0 ? [slot] : []));
  const last = minutes.at(-1);
  if (last != null && (last + 5) % 60 === 0) ticks.push(minutes.length);
  return ticks;
}

/** The minute from the event slot `slot` starts at; the window's end for the slot past the last. */
export function slotMinute(minutes: readonly number[], slot: number): number {
  return minutes[slot] ?? (minutes.at(-1) ?? 0) + 5;
}

/** The events behind a slot: those with a glucose reading in it. */
export function eventsAtSlot(events: readonly EventResponseEvent[], slot: number): EventResponseEvent[] {
  return events.filter((e) => covers(e.glucoseCoverage, slot));
}

export type EventSortKey = "at" | EventMetricKey;

/** Events ordered by `key`; events without the figure last, whichever the direction. */
export function sortEvents(
  events: readonly EventResponseEvent[],
  key: EventSortKey,
  descending: boolean
): EventResponseEvent[] {
  const value = (e: EventResponseEvent): number | null =>
    key === "at" ? new Date(e.at ?? 0).getTime() : (e.metrics?.[key] ?? null);
  return [...events].sort((a, b) => {
    const x = value(a);
    const y = value(b);
    if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1;
    return descending ? y - x : x - y;
  });
}
