import {
  GlucoseStatus,
  type TenantOverviewItem,
} from "#lib/api/generated/nocturne-api-client.js";
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

/** The page's URL with the open person set, or cleared when `slug` is null. */
export function personUrl(current: { href: string }, slug: string | null): URL {
  const url = new URL(current.href);
  if (slug) url.searchParams.set("person", slug);
  else url.searchParams.delete("person");
  return url;
}

/** The id of the open person's name in the panel, which names the side panel's landmark. */
export const PERSON_PANEL_HEADING_ID = "person-panel-title";
