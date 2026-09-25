/**
 * Groups glucose readings, noise clusters, and hypo events into per-local-day
 * buckets for the sensor data-quality strips. Each day is the patient's local
 * calendar day, so every timestamp is placed against that day's own midnight
 * rather than a single fixed offset. A band that crosses local midnight is
 * split into each day it touches, but counted once, on its start day.
 */
import type {
  SensorGlucose,
  GlucoseCluster,
  SensorIntegrityHypoEvent,
} from "$lib/api";
import { formatLocale } from "$lib/utils/formatting";
import { getLocalDayBoundariesUtc } from "$lib/utils/timezone";

const MIN_MS = 60_000;

export interface DayPoint {
  /** Minutes from local midnight. Clamped to 0–1440 on a 23- or 25-hour DST day. */
  x: number;
  /** Glucose in mg/dL. */
  y: number;
}

export interface DayBand {
  /** Minutes from local midnight, clamped to the day. */
  xStart: number;
  xEnd: number;
  cluster: GlucoseCluster;
}

export interface DayHypo {
  x: number;
  y: number;
  nocturnal: boolean;
}

export interface DayBucket {
  /**
   * Local midnight expressed as a UTC-epoch ms (for sorting, selection, and
   * date formatting).
   */
  dateMs: number;
  label: string;
  points: DayPoint[];
  bands: DayBand[];
  hypos: DayHypo[];
  clusterCount: number;
}

interface DayWindow {
  startMs: number;
  endMs: number;
}

function dayLabel(dateMs: number, timeZone: string): string {
  return new Date(dateMs).toLocaleDateString(formatLocale(), {
    timeZone,
    weekday: "short",
    month: "short",
    day: "numeric",
  });
}

function windowsFor(days: string[], timeZone: string): Map<string, DayWindow> {
  const windows = new Map<string, DayWindow>();
  for (const day of days) {
    const { start, end } = getLocalDayBoundariesUtc(day, timeZone);
    windows.set(day, { startMs: start.getTime(), endMs: end.getTime() });
  }
  return windows;
}

function windowContaining(
  windows: Map<string, DayWindow>,
  ms: number
): DayWindow | undefined {
  for (const window of windows.values()) {
    if (ms >= window.startMs && ms <= window.endMs) return window;
  }
  return undefined;
}

/**
 * Build per-local-day buckets from a report's entries, clusters, and hypo
 * events. `days` seeds a bucket for every day the report covers, so empty days
 * still render, and `timeZone` is the patient's IANA zone. A null zone falls
 * back to UTC.
 */
export function buildDayBuckets(
  entries: SensorGlucose[],
  clusters: GlucoseCluster[],
  hypoEvents: SensorIntegrityHypoEvent[],
  days: string[],
  timeZone: string | null
): DayBucket[] {
  const zone = timeZone ?? "UTC";
  const windows = windowsFor(days, zone);
  const buckets = new Map<number, DayBucket>();

  const ensure = (window: DayWindow): DayBucket => {
    let b = buckets.get(window.startMs);
    if (!b) {
      b = {
        dateMs: window.startMs,
        label: dayLabel(window.startMs, zone),
        points: [],
        bands: [],
        hypos: [],
        clusterCount: 0,
      };
      buckets.set(window.startMs, b);
    }
    return b;
  };

  for (const window of windows.values()) ensure(window);

  const clamp = (minutes: number) => Math.min(1440, minutes);

  for (const e of entries) {
    if (e.mills == null || e.mgdl == null) continue;
    const window = windowContaining(windows, e.mills);
    if (!window) continue;
    ensure(window).points.push({
      x: clamp((e.mills - window.startMs) / MIN_MS),
      y: e.mgdl,
    });
  }

  for (const c of clusters) {
    if (!c.start || !c.end) continue;
    const startMs = Date.parse(c.start);
    const endMs = Date.parse(c.end);
    if (Number.isNaN(startMs) || Number.isNaN(endMs)) continue;

    // A cluster spanning midnight is clipped into each day it touches, but counted
    // once (on its start day) so per-day counts sum to the report total.
    let counted = false;
    for (const window of windows.values()) {
      if (startMs > window.endMs || endMs < window.startMs) continue;
      const xStart = Math.max(0, (startMs - window.startMs) / MIN_MS);
      const xEnd = clamp((endMs - window.startMs) / MIN_MS);
      if (xEnd <= xStart) continue;
      const b = ensure(window);
      b.bands.push({ xStart, xEnd, cluster: c });
      if (!counted) {
        b.clusterCount++;
        counted = true;
      }
    }
  }

  for (const h of hypoEvents) {
    const t = h.event?.nadirTime;
    const y = h.event?.nadirMgdl;
    if (!t || y == null) continue;
    const ms = Date.parse(t);
    const window = windowContaining(windows, ms);
    if (!window) continue;
    ensure(window).hypos.push({
      x: clamp((ms - window.startMs) / MIN_MS),
      y,
      nocturnal: h.isNocturnal ?? false,
    });
  }

  return [...buckets.values()].sort((a, b) => a.dateMs - b.dateMs);
}
