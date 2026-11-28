import {
  GlucoseDirection,
  GlucoseStatus,
  NotificationUrgency,
  TenantOverviewAttentionKind,
  type TenantOverviewItem,
  type TenantOverviewResponse,
  type TenantReviewResponse,
} from "@nocturne/api-client";
import { makeReview, makeReviewEntry, makeStatistics, minutesAgo } from "./people-fixtures.js";
import { makeRuleReason, ruleAttention } from "./flag-rule-fixtures.js";

const GIVEN = ["Ava", "Ben", "Chloe", "Dev", "Ella", "Finn", "Grace", "Hugo", "Isla", "Jack", "Kira", "Liam"];
const FAMILY = ["Adams", "Brown", "Chen", "Davis", "Evans", "Garcia", "Ito", "Khan", "Lopez", "Nguyen"];
const DIRECTIONS = [
  GlucoseDirection.Flat,
  GlucoseDirection.FortyFiveUp,
  GlucoseDirection.SingleUp,
  GlucoseDirection.FortyFiveDown,
  GlucoseDirection.SingleDown,
  GlucoseDirection.DoubleDown,
];

/** Three hours of five-minute readings ending at `mgdl`. */
function trace(index: number, mgdl: number) {
  return Array.from({ length: 36 }, (_, step) => ({
    timestamp: minutesAgo(5 * (36 - step)),
    mgdl: Math.round(mgdl + 30 * Math.sin((index + step) / 5)),
  }));
}

function crowdPerson(index: number): TenantOverviewItem {
  const flagged = index % 3 === 0;
  const mgdl = flagged ? 62 : 100 + ((index * 37) % 120);
  const slug = `p${String(index).padStart(3, "0")}`;
  return {
    tenantId: `id-${slug}`,
    slug,
    displayName: `${GIVEN[index % GIVEN.length]} ${FAMILY[index % FAMILY.length]} ${index}`,
    lastReadingAt: minutesAgo(4),
    latest: {
      mgdl,
      delta: (index % 9) - 4,
      direction: DIRECTIONS[index % DIRECTIONS.length],
      timestamp: minutesAgo(4),
    },
    status: flagged ? GlucoseStatus.Low : GlucoseStatus.InRange,
    thresholds: { urgentLow: 54, low: 70, high: 180, urgentHigh: 250 },
    activeAlertCount: flagged ? 1 : 0,
    recentReadings: trace(index, mgdl),
    therapy: { iob: 1.5, iobKnown: true, cob: 20, cobKnown: true },
    devices: {
      reservoirUnits: 40 + (index % 160),
      pumpBatteryPercent: 20 + (index % 80),
      snapshotAt: minutesAgo(180),
    },
    attention: flagged
      ? [
          { kind: TenantOverviewAttentionKind.GlucoseOutOfRange, level: NotificationUrgency.Hazard },
          { kind: TenantOverviewAttentionKind.ActiveAlert, level: NotificationUrgency.Warn },
          ruleAttention(makeRuleReason({ value: 4 + (index % 7) })),
        ]
      : [],
  };
}

/** The API's order: the flagged first, then everyone, each by name. */
export function inApiOrder(people: TenantOverviewItem[]): TenantOverviewItem[] {
  const byName = (a: TenantOverviewItem, b: TenantOverviewItem) =>
    (a.displayName ?? "").localeCompare(b.displayName ?? "");
  const flagged = people.filter((person) => (person.attention?.length ?? 0) > 0).sort(byName);
  const others = people.filter((person) => (person.attention?.length ?? 0) === 0).sort(byName);
  return [...flagged, ...others];
}

/** A clinic's roster: `count` people with a reading, a trace, pump figures, and a third of them flagged. */
export function crowd(count: number): TenantOverviewItem[] {
  return inApiOrder(Array.from({ length: count }, (_, index) => crowdPerson(index)));
}

/** A fresh answer of the same people, parsed anew as the API's JSON always is. */
export function answerOf(people: TenantOverviewItem[]): TenantOverviewResponse {
  return { tenants: structuredClone(people) };
}

/**
 * The next poll's people: new readings for the `changed`, and `flagged` newly given a reason, in the API's order.
 * Everyone else holds the same values as before.
 */
export function nextPoll(
  people: TenantOverviewItem[],
  changed: string[],
  flagged: string
): TenantOverviewItem[] {
  return inApiOrder(
    structuredClone(people).map((person) => {
      if (person.slug === flagged) {
        return {
          ...person,
          attention: [ruleAttention(makeRuleReason({ value: 9 }), NotificationUrgency.Hazard)],
        };
      }
      if (!person.slug || !changed.includes(person.slug)) return person;
      const mgdl = (person.latest?.mgdl ?? 100) + 7;
      return {
        ...person,
        latest: { ...person.latest, mgdl, delta: 7, timestamp: minutesAgo(0) },
        lastReadingAt: minutesAgo(0),
        recentReadings: [...(person.recentReadings ?? []).slice(1), { timestamp: minutesAgo(0), mgdl }],
      };
    })
  );
}

/** The number a crowd person was made from, which their figures follow. */
export function crowdIndex(person: TenantOverviewItem): number {
  return Number(person.slug?.slice(1));
}

/** The review of `people`, in the order given. */
export function crowdReview(people: TenantOverviewItem[]): TenantReviewResponse {
  return makeReview(
    people.map((person) => {
      const index = crowdIndex(person);
      return makeReviewEntry(person, {
        statistics: makeStatistics({
          timeInRange: { ...makeStatistics().timeInRange, target: 40 + (index % 50) },
          gmi: 6 + (index % 30) / 10,
          coefficientOfVariation: 25 + (index % 20),
          cgmActivePercent: 60 + (index % 40),
          timeInRangeChange: (index % 21) - 10,
        }),
      });
    })
  );
}
