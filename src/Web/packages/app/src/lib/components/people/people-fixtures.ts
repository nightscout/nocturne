import {
  GlucoseDirection,
  GlucoseStatus,
  type TenantOverviewItem,
} from "#lib/api/generated/nocturne-api-client.js";

const MINUTE_MS = 60_000;

export function minutesAgo(minutes: number): string {
  return new Date(Date.now() - minutes * MINUTE_MS).toISOString();
}

export function makePerson(
  overrides: Partial<TenantOverviewItem> = {}
): TenantOverviewItem {
  const slug = overrides.slug ?? "alice";
  return {
    tenantId: `id-${slug}`,
    slug,
    displayName: slug.charAt(0).toUpperCase() + slug.slice(1),
    lastReadingAt: minutesAgo(5),
    latest: {
      mgdl: 120,
      delta: 4,
      direction: GlucoseDirection.Flat,
      timestamp: minutesAgo(5),
    },
    status: GlucoseStatus.InRange,
    thresholds: { urgentLow: 54, low: 70, high: 180, urgentHigh: 250 },
    activeAlertCount: 0,
    recentReadings: [
      { timestamp: minutesAgo(60), mgdl: 110 },
      { timestamp: minutesAgo(30), mgdl: 115 },
      { timestamp: minutesAgo(5), mgdl: 120 },
    ],
    therapy: { iob: 1.5, iobKnown: true, cob: 20, cobKnown: true },
    devices: { reservoirUnits: 120, pumpBatteryPercent: 80 },
    attention: [],
    ...overrides,
  };
}
