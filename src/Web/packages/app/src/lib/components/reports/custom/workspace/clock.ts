import {
  chartFormat,
  formatMediumDateRange,
  time,
} from "#lib/utils/formatting.js";

/**
 * A minute of the day, 0 to 1440, in the member's clock format; 1440 is the end
 * of the day.
 */
export function minuteLabel(minute: number): string {
  if (minute >= 1440)
    return chartFormat().hour12 ? time(new Date(2000, 0, 2, 0, 0)) : "24:00";
  return time(new Date(2000, 0, 1, Math.floor(minute / 60), minute % 60));
}

/**
 * A window of the day as words: "06:00 to 08:30". An end of 0 after a later
 * start is midnight.
 */
export function windowLabel(from: number, to: number): string {
  return `${minuteLabel(from)} to ${minuteLabel(to === 0 && from > 0 ? 1440 : to)}`;
}

/** Two local dates, yyyy-MM-dd, in the app's medium date format. */
export function spanLabel(span: { from: string; to: string }): string {
  return formatMediumDateRange(
    new Date(`${span.from}T12:00:00`),
    new Date(`${span.to}T12:00:00`)
  );
}

/** A window of the day in minutes, 0 to 1439 each; a start after the end runs past midnight. */
export interface DayWindow {
  fromMinute: number;
  toMinute: number;
}

/** The parts of the day a window can be set to in one step. */
export const DAY_PARTS = [
  { id: "overnight", fromMinute: 1320, toMinute: 360 },
  { id: "morning", fromMinute: 360, toMinute: 720 },
  { id: "afternoon", fromMinute: 720, toMinute: 1080 },
  { id: "evening", fromMinute: 1080, toMinute: 1320 },
] as const;

export type DayPart = (typeof DAY_PARTS)[number]["id"];

/** The part of the day a window is exactly, or null. An end of 0 and of 1440 are both midnight. */
export function dayPartOf(window: DayWindow): DayPart | null {
  const to = window.toMinute % 1440;
  return DAY_PARTS.find((p) => p.fromMinute === window.fromMinute && p.toMinute === to)?.id ?? null;
}

/** The minutes a start or an end can be set to: every 15 minutes, and `keep` when it is between them. */
export function windowMinutes(keep: number[] = []): number[] {
  const steps = Array.from({ length: 96 }, (_, i) => i * 15);
  const extra = keep.map((m) => m % 1440).filter((m) => m >= 0 && m < 1440 && !steps.includes(m));
  return [...steps, ...extra].sort((a, b) => a - b);
}

/**
 * The window with its start (`edge` "from") or end moved to `minute`, or null when that makes the start
 * and the end the same minute, which is no window. An end at midnight is 0.
 */
export function moveWindow(window: DayWindow, edge: "from" | "to", minute: number): DayWindow | null {
  const m = ((minute % 1440) + 1440) % 1440;
  const next = edge === "from" ? { fromMinute: m, toMinute: window.toMinute % 1440 } : { fromMinute: window.fromMinute, toMinute: m };
  return next.fromMinute === next.toMinute ? null : next;
}

/** Whether a window runs past midnight: its start is after its end, and the end is not midnight itself. */
export function crossesMidnight(window: DayWindow): boolean {
  const to = window.toMinute % 1440;
  return to !== 0 && window.fromMinute > to;
}
