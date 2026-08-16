/**
 * Maps sleep sessions to the calendar day they display under, shared by the
 * trends page's composition chart and actogram row links.
 *
 * Nights are bucketed using the backend's "noon rule": a session starting
 * after noon belongs to the following calendar day, so a bedtime of 11pm and
 * one of 1am on the same night both land on the same display day.
 */

import { toDate } from "./formatting";

const MS_PER_HOUR = 60 * 60 * 1000;

/** Local-midnight timestamp for a given Date (used as a map key). */
export function dayKeyFor(date: Date): number {
  return new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();
}

/** The display-day key (local midnight, ms) a night's `inBedAt` bucket to. */
export function nightDisplayDayKey(inBedAt: Date | string | undefined | null): number | null {
  const inBed = toDate(inBedAt);
  if (!inBed) return null;
  return dayKeyFor(new Date(inBed.getTime() - 12 * MS_PER_HOUR));
}

/**
 * Maps each display-day key to its night. When more than one night falls on
 * the same display day (e.g. an unfiltered source view with multiple device
 * sessions per night), the first one wins — a display-only tie-break, not a
 * domain calculation.
 */
export function buildNightsByDayKey<T extends { inBedAt?: Date | string }>(
  nights: readonly T[]
): Map<number, T> {
  const map = new Map<number, T>();
  for (const night of nights) {
    const key = nightDisplayDayKey(night.inBedAt);
    if (key == null) continue;
    if (!map.has(key)) map.set(key, night);
  }
  return map;
}
