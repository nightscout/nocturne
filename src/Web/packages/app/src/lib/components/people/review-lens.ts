import {
  TenantReviewUnavailable,
  type TenantOverviewItem,
  type TenantReviewPerson,
  type TenantReviewResponse,
  type TenantReviewStatistics,
} from "@nocturne/api-client";

export type Lens = "now" | "review";

/** The periods the review read accepts, in days. */
export const REVIEW_DAYS = [14, 30, 90] as const;

export type ReviewDays = (typeof REVIEW_DAYS)[number];

export const DEFAULT_REVIEW_DAYS: ReviewDays = 14;

export function parseLens(value: string | null): Lens {
  return value === "review" ? "review" : "now";
}

export function parseDays(value: string | null): ReviewDays {
  const days = Number(value);
  return REVIEW_DAYS.find((allowed) => allowed === days) ?? DEFAULT_REVIEW_DAYS;
}

export type ReviewState =
  | { status: "loading" }
  | { status: "failed" }
  | {
      status: "ready";
      response: TenantReviewResponse;
      days: ReviewDays;
      /** The response is being read again, so a person it lacks may be in the next one. */
      refreshing: boolean;
    };

/** What one person's data cells show in the Review lens. */
export type ReviewCell =
  | { kind: "loading" }
  | { kind: "read-failed" }
  | { kind: "unavailable"; reason: TenantReviewUnavailable }
  | { kind: "missing" }
  | { kind: "empty" }
  | { kind: "figures"; statistics: TenantReviewStatistics; days: ReviewDays };

function hasNoFigures(statistics: TenantReviewStatistics): boolean {
  return (
    statistics.timeInRange == null &&
    statistics.gmi == null &&
    statistics.coefficientOfVariation == null &&
    statistics.cgmActivePercent == null &&
    statistics.timeInRangeChange == null
  );
}

export function reviewCell(
  review: ReviewState,
  entry: TenantReviewPerson | undefined
): ReviewCell {
  if (review.status === "loading") return { kind: "loading" };
  if (review.status === "failed") return { kind: "read-failed" };
  if (!entry) return review.refreshing ? { kind: "loading" } : { kind: "missing" };
  if (entry.unavailable) {
    return { kind: "unavailable", reason: entry.unavailable };
  }
  if (!entry.statistics) return { kind: "missing" };
  if (hasNoFigures(entry.statistics)) return { kind: "empty" };
  return { kind: "figures", statistics: entry.statistics, days: review.days };
}

/** The verdict's own flag: the period does not meet the reliability criteria. */
export function isNotReliable(statistics: TenantReviewStatistics): boolean {
  return statistics.reliability?.meetsReliabilityCriteria === false;
}

/**
 * The people in the review's own order, which a sort by a review figure decides: each person the review
 * holds, then any person it lacks, in the list's order.
 */
export function inReviewOrder(people: TenantOverviewItem[], response: TenantReviewResponse): TenantOverviewItem[] {
  const byTenant = new Map(people.flatMap((person) => (person.tenantId ? [[person.tenantId, person] as const] : [])));
  const reviewed = (response.people ?? []).flatMap((entry) => {
    const person = entry.tenantId ? byTenant.get(entry.tenantId) : undefined;
    return person ? [person] : [];
  });
  const placed = new Set(reviewed);
  return [...reviewed, ...people.flatMap((person) => (placed.has(person) ? [] : [person]))];
}
