import { hourLabel } from "$lib/utils/formatting";

const atHour = (hour: number) => new Date(2000, 0, 1, hour);

/** The hour's start on the viewer's clock format, e.g. "2 AM" or "02". */
export function hourStart(hour: number): string {
  return hourLabel(atHour(hour));
}

/** The hour as a span, e.g. "2 AM–3 AM"; the last hour ends at midnight. */
export function hourSpan(hour: number): string {
  return `${hourLabel(atHour(hour))}–${hourLabel(atHour(hour + 1))}`;
}
