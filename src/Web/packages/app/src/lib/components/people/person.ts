import {
  GlucoseStatus,
  type TenantOverviewItem,
} from "@nocturne/api-client";
import { tenantUrl } from "#lib/utils/tenant-host.js";

export function personName(person: TenantOverviewItem): string {
  return person.displayName || person.slug || "";
}

export function personHref(
  person: TenantOverviewItem,
  baseDomain: string | null
): string | null {
  return person.slug && baseDomain ? tenantUrl(person.slug, baseDomain) : null;
}

function hasReading(person: TenantOverviewItem): boolean {
  return person.latest?.mgdl != null;
}

/** A reading the server calls stale, or none at all. */
export function readingIsNotCurrent(person: TenantOverviewItem): boolean {
  return person.status === GlucoseStatus.Stale || !hasReading(person);
}

/** How often the People page re-reads the overview, and a person's panel while it is open. */
export const PERSON_POLL_MS = 60_000;

/** The id of the open person's name in the panel, which names the side panel's landmark. */
export const PERSON_PANEL_HEADING_ID = "person-panel-title";

/** Whether the API gave the person a reason to need attention. */
export function isFlagged(person: TenantOverviewItem): boolean {
  return (person.attention?.length ?? 0) > 0;
}
