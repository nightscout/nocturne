import {
  TenantReviewUnavailable,
  type TenantReviewPerson,
  type TenantReviewResponse,
  type TenantReviewStatistics,
} from "#lib/api/generated/nocturne-api-client.js";

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

/** What the page shows, which is everything its address states. */
export interface PageView {
  lens: Lens;
  days: ReviewDays;
  person: string | null;
}

/** The page's address for a view. The Now lens carries no period. */
export function pageUrl(current: { href: string }, view: PageView): URL {
  const url = new URL(current.href);
  url.searchParams.delete("lens");
  url.searchParams.delete("days");
  url.searchParams.delete("person");
  if (view.lens === "review") {
    url.searchParams.set("lens", view.lens);
    url.searchParams.set("days", String(view.days));
  }
  if (view.person) url.searchParams.set("person", view.person);
  return url;
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
