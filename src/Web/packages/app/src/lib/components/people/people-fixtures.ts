import {
  GlucoseDirection,
  GlucoseStatus,
  TenantPersonEntryKind,
  type TenantOverviewItem,
  type TenantPersonDetail,
  type TenantReviewPerson,
  type TenantReviewResponse,
  type TenantReviewStatistics,
} from "@nocturne/api-client";

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

export function makeStatistics(
  overrides: Partial<TenantReviewStatistics> = {}
): TenantReviewStatistics {
  return {
    timeInRange: {
      veryLow: 1,
      low: 3,
      target: 62,
      tightTarget: 40,
      high: 24,
      veryHigh: 10,
      belowRange: 4,
      aboveRange: 34,
    },
    gmi: 7.1,
    coefficientOfVariation: 34,
    cgmActivePercent: 91,
    reliability: { meetsReliabilityCriteria: true, daysOfData: 14 },
    timeInRangeChange: 4,
    ...overrides,
  };
}

export function makeReviewEntry(
  person: TenantOverviewItem,
  overrides: Partial<TenantReviewPerson> = {}
): TenantReviewPerson {
  return {
    tenantId: person.tenantId,
    slug: person.slug,
    displayName: person.displayName,
    statistics: makeStatistics(),
    ...overrides,
  };
}

export function makeReview(
  people: TenantReviewPerson[]
): TenantReviewResponse {
  return {
    from: "2026-11-07T00:00:00Z",
    to: "2026-11-21T00:00:00Z",
    previousFrom: "2026-10-24T00:00:00Z",
    previousTo: "2026-11-07T00:00:00Z",
    people,
  };
}

export function makeDetail(
  person: TenantOverviewItem,
  overrides: Partial<TenantPersonDetail> = {}
): TenantPersonDetail {
  return {
    person,
    readings: [
      { timestamp: minutesAgo(600), mgdl: 95 },
      { timestamp: minutesAgo(300), mgdl: 140 },
      { timestamp: minutesAgo(5), mgdl: 120 },
    ],
    entries: [
      { kind: TenantPersonEntryKind.Bolus, timestamp: minutesAgo(30), units: 4.5 },
      { kind: TenantPersonEntryKind.Carbs, timestamp: minutesAgo(90), grams: 45 },
    ],
    from: minutesAgo(24 * 60),
    to: minutesAgo(0),
    ...overrides,
  };
}
