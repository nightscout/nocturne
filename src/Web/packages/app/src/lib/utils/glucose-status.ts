/**
 * The server Status field is the only source of truth for classification —
 * never re-derive status from mg/dL values on the frontend.
 */

import {
  GlucoseStatus,
  type TenantOverviewItem,
} from "$lib/api/generated/nocturne-api-client";

export const glucoseStatusStyles: Record<GlucoseStatus, string> = {
  [GlucoseStatus.UrgentLow]: "text-glucose-very-low",
  [GlucoseStatus.Low]: "text-glucose-low",
  [GlucoseStatus.InRange]: "text-glucose-in-range",
  [GlucoseStatus.High]: "text-glucose-high",
  [GlucoseStatus.UrgentHigh]: "text-glucose-very-high",
  [GlucoseStatus.Stale]: "text-muted-foreground",
  [GlucoseStatus.Unknown]: "text-muted-foreground",
};

/**
 * Look a status up in a per-status table, tolerating enum values this client
 * build doesn't know yet (server deployed ahead of the web image) as well as a
 * missing status, by falling back to Unknown.
 */
function lookup<T>(
  table: Record<GlucoseStatus, T>,
  status: GlucoseStatus | undefined
): T {
  return (status && table[status]) ?? table[GlucoseStatus.Unknown];
}

export function getGlucoseStatusClass(
  status: GlucoseStatus | undefined
): string {
  return lookup(glucoseStatusStyles, status);
}

/** Presentational rank only — not a domain ordering. */
export const glucoseStatusSortOrder: Record<GlucoseStatus, number> = {
  [GlucoseStatus.UrgentLow]: 0,
  [GlucoseStatus.UrgentHigh]: 1,
  [GlucoseStatus.Low]: 2,
  [GlucoseStatus.High]: 3,
  [GlucoseStatus.Stale]: 4,
  [GlucoseStatus.InRange]: 5,
  [GlucoseStatus.Unknown]: 6,
};

export function sortTenantsByUrgency(
  tenants: TenantOverviewItem[]
): TenantOverviewItem[] {
  return [...tenants].sort((a, b) => {
    const rankA = lookup(glucoseStatusSortOrder, a.status);
    const rankB = lookup(glucoseStatusSortOrder, b.status);
    if (rankA !== rankB) return rankA - rankB;
    return (a.displayName || a.slug || "").localeCompare(
      b.displayName || b.slug || ""
    );
  });
}
