import { timeDay } from "d3-time";

/** The current instant as an ISO 8601 string. */
export function isoNow(): string {
  return new Date().toISOString();
}

/** From `fromMs` to now, as the ISO 8601 pair a range query takes. */
export function untilNow(fromMs: number): { from: string; to: string } {
  return { from: new Date(fromMs).toISOString(), to: isoNow() };
}

/** Midnight at the start of `ms`'s day in the browser's time zone, as epoch milliseconds. */
export function startOfLocalDay(ms: number): number {
  return timeDay.floor(new Date(ms)).getTime();
}
