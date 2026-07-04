/**
 * Mapping from the server-computed GlucoseStatus to presentation classes.
 *
 * The server Status field is the only source of truth for classification —
 * never re-derive status from mg/dL values on the frontend.
 */

import {
  GlucoseStatus,
  type TenantOverviewItem,
} from "$lib/api/generated/nocturne-api-client";

export interface GlucoseStatusStyle {
  /** Tailwind text color class for the glucose value. */
  text: string;
  /** Tailwind background color class (e.g. status dot). */
  bg: string;
}

export const glucoseStatusStyles: Record<GlucoseStatus, GlucoseStatusStyle> = {
  [GlucoseStatus.UrgentLow]: {
    text: "text-glucose-very-low",
    bg: "bg-glucose-very-low",
  },
  [GlucoseStatus.Low]: { text: "text-glucose-low", bg: "bg-glucose-low" },
  [GlucoseStatus.InRange]: {
    text: "text-glucose-in-range",
    bg: "bg-glucose-in-range",
  },
  [GlucoseStatus.High]: { text: "text-glucose-high", bg: "bg-glucose-high" },
  [GlucoseStatus.UrgentHigh]: {
    text: "text-glucose-very-high",
    bg: "bg-glucose-very-high",
  },
  [GlucoseStatus.Stale]: {
    text: "text-muted-foreground",
    bg: "bg-muted-foreground",
  },
  [GlucoseStatus.Unknown]: {
    text: "text-muted-foreground",
    bg: "bg-muted-foreground",
  },
};

/**
 * Style for a server status, tolerating enum values this client build doesn't
 * know yet (server deployed ahead of the web image) by falling back to
 * Unknown.
 */
export function getGlucoseStatusStyle(
  status: GlucoseStatus | undefined
): GlucoseStatusStyle {
  return (
    (status && glucoseStatusStyles[status]) ??
    glucoseStatusStyles[GlucoseStatus.Unknown]
  );
}

/** Presentational sort rank: most urgent statuses first. */
export const glucoseStatusSortOrder: Record<GlucoseStatus, number> = {
  [GlucoseStatus.UrgentLow]: 0,
  [GlucoseStatus.UrgentHigh]: 1,
  [GlucoseStatus.Low]: 2,
  [GlucoseStatus.High]: 3,
  [GlucoseStatus.Stale]: 4,
  [GlucoseStatus.InRange]: 5,
  [GlucoseStatus.Unknown]: 6,
};

function statusRank(status: GlucoseStatus | undefined): number {
  // Unknown-rank fallback covers both missing status and unrecognized enum values.
  return (
    (status !== undefined ? glucoseStatusSortOrder[status] : undefined) ??
    glucoseStatusSortOrder[GlucoseStatus.Unknown]
  );
}

/** Presentational sort: status urgency first, then display name. */
export function sortTenantsByUrgency(
  tenants: TenantOverviewItem[]
): TenantOverviewItem[] {
  return [...tenants].sort((a, b) => {
    const rankA = statusRank(a.status);
    const rankB = statusRank(b.status);
    if (rankA !== rankB) return rankA - rankB;
    return (a.displayName || a.slug || "").localeCompare(
      b.displayName || b.slug || ""
    );
  });
}
