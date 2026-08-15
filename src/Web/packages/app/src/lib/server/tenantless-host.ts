/**
 * Tenantless-host classification.
 *
 * Most hosts name a tenant ({slug}.{base-domain}). Two do not, and both serve the
 * cross-tenant caregiver dashboard instead of a single tenant's app:
 *
 * - the apex (the base domain itself), which is what a self-hosted family deployment uses;
 * - a reserved dashboard slug (dashboard.{base-domain}, app.{base-domain}), for hosted
 *   deployments whose apex is already taken by a marketing site.
 *
 * Classification is suffix-based throughout, mirroring extractTenantSlug and the API's
 * SubdomainParser: `nocturne.example.com` is as valid a base domain as `example.com`, so
 * hostname label counts carry no meaning.
 */

import { extractTenantSlug, isShareHost } from "./request-host";

/**
 * Reserved slugs that serve the dashboard rather than a tenant, when DASHBOARD_SLUGS is unset.
 */
export const DEFAULT_DASHBOARD_SLUGS = ["dashboard", "app"];

/**
 * Parse the DASHBOARD_SLUGS env var (comma-separated). An unset value takes the defaults; an
 * explicitly empty value reserves nothing, so the apex alone serves the dashboard.
 */
export function parseDashboardSlugs(raw: string | null | undefined): string[] {
  if (raw === null || raw === undefined) return DEFAULT_DASHBOARD_SLUGS;
  return raw
    .split(",")
    .map((slug) => slug.trim().toLowerCase())
    .filter((slug) => slug.length > 0);
}

export type HostKind = "tenantless" | "tenant" | "share" | "unknown";

/**
 * Classify a request host against the base domain.
 *
 * - "tenantless" — the apex, or a reserved dashboard slug: serves the cross-tenant dashboard.
 * - "tenant"     — {slug}.{base-domain}: serves that tenant's app. `slug` is set.
 * - "share"      — {token}.share.{base-domain}: the anonymous read-only view.
 * - "unknown"    — an unrelated host, or no base domain configured.
 */
export function classifyHost(
  host: string | null | undefined,
  baseDomain: string | null | undefined,
  dashboardSlugs: readonly string[] = DEFAULT_DASHBOARD_SLUGS
): { kind: HostKind; slug: string | null } {
  if (!host || !baseDomain) return { kind: "unknown", slug: null };

  const hostname = host.split(":")[0]!;
  const baseHostname = baseDomain.split(":")[0]!;
  if (!baseHostname) return { kind: "unknown", slug: null };

  if (hostname.toLowerCase() === baseHostname.toLowerCase()) {
    return { kind: "tenantless", slug: null };
  }

  // Checked before the slug split: a share host's "{token}.share" is not a tenant slug, and
  // must never be mistaken for a reserved dashboard slug either.
  if (isShareHost(host)) return { kind: "share", slug: null };

  const slug = extractTenantSlug(host, baseDomain);
  if (!slug) return { kind: "unknown", slug: null };

  if (dashboardSlugs.includes(slug.toLowerCase())) {
    return { kind: "tenantless", slug: null };
  }

  return { kind: "tenant", slug };
}

/** Whether the host serves the cross-tenant dashboard rather than a single tenant's app. */
export function isTenantlessHost(
  host: string | null | undefined,
  baseDomain: string | null | undefined,
  dashboardSlugs: readonly string[] = DEFAULT_DASHBOARD_SLUGS
): boolean {
  return classifyHost(host, baseDomain, dashboardSlugs).kind === "tenantless";
}
