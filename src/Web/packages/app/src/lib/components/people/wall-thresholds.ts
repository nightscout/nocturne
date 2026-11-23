import type {
  TenantOverviewItem,
  TenantOverviewThresholds,
} from "@nocturne/api-client";

export interface RangeBounds {
  urgentLow: number;
  low: number;
  high: number;
  urgentHigh: number;
}

function boundsOf(thresholds: TenantOverviewThresholds | undefined): RangeBounds | null {
  const { urgentLow, low, high, urgentHigh } = thresholds ?? {};
  return urgentLow == null || low == null || high == null || urgentHigh == null
    ? null
    : { urgentLow, low, high, urgentHigh };
}

/** The thresholds every person holds, or null when two differ or one has none. */
export function sharedThresholds(people: TenantOverviewItem[]): RangeBounds | null {
  const first = boundsOf(people[0]?.thresholds);
  if (!first) return null;
  return people.every((person) => {
    const own = boundsOf(person.thresholds);
    return (
      own !== null &&
      own.urgentLow === first.urgentLow &&
      own.low === first.low &&
      own.high === first.high &&
      own.urgentHigh === first.urgentHigh
    );
  })
    ? first
    : null;
}
