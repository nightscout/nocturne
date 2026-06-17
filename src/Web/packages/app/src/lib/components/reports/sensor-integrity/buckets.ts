/**
 * Groups glucose readings, noise clusters, and hypo events into per-local-day buckets for the
 * sensor data-quality strips. All times are placed using a single display UTC offset (the first
 * reading's offset) so the glucose trace and the cluster bands stay aligned within a row.
 */
import type { SensorGlucose, GlucoseCluster, SensorIntegrityHypoEvent } from "$lib/api";

const DAY_MS = 86_400_000;
const MIN_MS = 60_000;

// The generated client types DTO date fields as `Date`, but they are ISO strings at
// runtime (the client deserializes with no reviver). Re-wrap before reading epoch ms.
const epochMs = (d: Date): number => new Date(d).getTime();

export interface DayPoint {
  /** Minutes from local midnight (0–1440). */
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
  /** Local midnight expressed as a UTC-epoch ms (for date formatting only). */
  dateMs: number;
  label: string;
  points: DayPoint[];
  bands: DayBand[];
  hypos: DayHypo[];
  clusterCount: number;
}

function resolveDisplayOffsetMinutes(entries: SensorGlucose[]): number {
  for (const e of entries) {
    if (e.utcOffset != null) return e.utcOffset;
  }
  return 0;
}

function dayLabel(dateMs: number): string {
  return new Date(dateMs).toLocaleDateString(undefined, {
    timeZone: "UTC",
    weekday: "short",
    month: "short",
    day: "numeric",
  });
}

/** Build per-local-day buckets from a report's entries, clusters, and hypo events. */
export function buildDayBuckets(
  entries: SensorGlucose[],
  clusters: GlucoseCluster[],
  hypoEvents: SensorIntegrityHypoEvent[]
): DayBucket[] {
  const offsetMs = resolveDisplayOffsetMinutes(entries) * MIN_MS;
  const buckets = new Map<number, DayBucket>();

  const ensure = (dayKey: number): DayBucket => {
    let b = buckets.get(dayKey);
    if (!b) {
      const dateMs = dayKey * DAY_MS;
      b = {
        dateMs,
        label: dayLabel(dateMs),
        points: [],
        bands: [],
        hypos: [],
        clusterCount: 0,
      };
      buckets.set(dayKey, b);
    }
    return b;
  };

  for (const e of entries) {
    if (e.mills == null || e.mgdl == null) continue;
    const localMs = e.mills + offsetMs;
    const dayKey = Math.floor(localMs / DAY_MS);
    ensure(dayKey).points.push({ x: (localMs - dayKey * DAY_MS) / MIN_MS, y: e.mgdl });
  }

  for (const c of clusters) {
    if (!c.start || !c.end) continue;
    const startLocal = epochMs(c.start) + offsetMs;
    const endLocal = epochMs(c.end) + offsetMs;
    const firstDay = Math.floor(startLocal / DAY_MS);
    const lastDay = Math.floor(endLocal / DAY_MS);

    // A cluster spanning midnight is clipped into each day it touches, but counted
    // once (on its start day) so per-day counts sum to the report total.
    for (let dayKey = firstDay; dayKey <= lastDay; dayKey++) {
      const dayStart = dayKey * DAY_MS;
      const xStart = Math.max(0, (startLocal - dayStart) / MIN_MS);
      const xEnd = Math.min(1440, (endLocal - dayStart) / MIN_MS);
      if (xEnd <= xStart) continue;
      const b = ensure(dayKey);
      b.bands.push({ xStart, xEnd, cluster: c });
      if (dayKey === firstDay) b.clusterCount++;
    }
  }

  for (const h of hypoEvents) {
    const t = h.event?.nadirTime;
    const y = h.event?.nadirMgdl;
    if (!t || y == null) continue;
    const localMs = epochMs(t) + offsetMs;
    const dayKey = Math.floor(localMs / DAY_MS);
    ensure(dayKey).hypos.push({
      x: (localMs - dayKey * DAY_MS) / MIN_MS,
      y,
      nocturnal: h.isNocturnal ?? false,
    });
  }

  return [...buckets.values()].sort((a, b) => a.dateMs - b.dateMs);
}
