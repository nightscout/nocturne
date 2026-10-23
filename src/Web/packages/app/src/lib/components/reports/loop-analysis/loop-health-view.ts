import type {
  LoopHealthDistribution,
  LoopHealthGap,
  LoopHealthGaps,
  LoopHealthHourValue,
  LoopHealthShare,
  LoopHealthShareHour,
} from "$lib/api";
import { clockTime, SLOT_MINUTES } from "./daily-profile-view";

const SLOTS_PER_HOUR = 60 / SLOT_MINUTES;

/** Fill opacities the marks and their key share, so a swatch looks like the mark it names. */
export const BAND_FILL = 0.3;
export const SPARSE_BAR_FILL = 0.25;
export const SPARSE_BAND_FILL = 0.08;

export const hourOfSlot = (slot: number) => Math.floor(slot / SLOTS_PER_HOUR);

/** The slot span an hour covers on the shared time-of-day axis. */
export const hourSpan = (hour: number) => ({ start: hour * SLOTS_PER_HOUR, end: (hour + 1) * SLOTS_PER_HOUR });

export function shareHour(share: LoopHealthShare | null | undefined, hour: number): LoopHealthShareHour | undefined {
  return share?.hours?.find((h) => h.hour === hour);
}

export function distributionHour(
  distribution: LoopHealthDistribution | null | undefined,
  hour: number
): LoopHealthHourValue | undefined {
  return distribution?.hours?.find((h) => h.hour === hour);
}

export interface HourBar {
  hour: number;
  value: number;
  sparse: boolean;
}

/** A share's hours as bars; an hour with nothing measured has none, and a zero is a bar. */
export function shareBars(share: LoopHealthShare | null | undefined): HourBar[] {
  return (share?.hours ?? [])
    .filter((h) => h.percent != null)
    .map((h) => ({ hour: h.hour ?? 0, value: h.percent!, sparse: h.daily?.sparse ?? true }));
}

/** A kind of gap's list, longest first; the API serves it oldest first. */
export function longestFirst(gaps: LoopHealthGaps | null | undefined): LoopHealthGap[] {
  return [...(gaps?.gaps ?? [])].sort((a, b) => (b.minutes ?? 0) - (a.minutes ?? 0));
}

/** The gaps that began in a local hour of the day. */
export function beganIn(gaps: LoopHealthGap[], hour: number | null): LoopHealthGap[] {
  return hour == null ? gaps : gaps.filter((g) => g.startMinuteOfDay != null && Math.floor(g.startMinuteOfDay / 60) === hour);
}

/** The Day in Review search for the local day and time a gap began at. */
export function dayInReviewSearch(gap: LoopHealthGap): string {
  return `date=${gap.date ?? ""}&time=${clockTime(gap.startMinuteOfDay ?? 0)}`;
}
