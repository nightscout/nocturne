/**
 * Tenantless-host classification.
 *
 * Most hosts name a tenant ({slug}.{base-domain}). Two do not, and either may serve the
 * cross-tenant caregiver dashboard instead of a single tenant's app:
 *
 * - a reserved dashboard slug (opt-in via DASHBOARD_SLUGS, e.g. dashboard.{base-domain}), for
 *   hosted deployments whose apex is already taken by a marketing site — always the dashboard;
 * - the apex (the base domain itself) — the dashboard only when it resolves no tenant.
 *   The API auto-resolves a sole tenant on the apex, which is how a single-tenant
 *   self-hosted install serves the whole app at https://nocturne.example.com/, so the
 *   hostname alone cannot decide this one; the status endpoint answers it.
 *
 * Classification is suffix-based throughout, mirroring extractTenantSlug and the API's
 * SubdomainParser: `nocturne.example.com` is as valid a base domain as `example.com`, so
 * hostname label counts carry no meaning.
 *
 * Reserving a slug is opt-in, and only once first-run setup is complete. A reserved slug names no
 * tenant, and the API answers a host that resolves no tenant with 404 whether the instance holds
 * zero tenants or a thousand — so the fresh-install signal (the 503 from tenant resolution) never
 * reaches the web app on such a host, and an operator who opens the reserved host on a brand-new
 * instance is sent to sign in on an instance that has no accounts yet. Complete setup on the apex
 * or on the tenant's own host first, then set DASHBOARD_SLUGS. Nothing is lost by leaving it
 * unset: the apex serves the dashboard whenever it resolves no tenant either way.
 */

import { extractTenantSlug, isShareHost } from "./request-host";

/**
 * Parse the DASHBOARD_SLUGS env var (comma-separated). Unset or empty reserves nothing, so the
 * apex alone serves the dashboard.
 */
export function parseDashboardSlugs(raw: string | null | undefined): string[] {
  if (!raw) return [];
  return raw
    .split(",")
    .map((slug) => slug.trim().toLowerCase())
    .filter((slug) => slug.length > 0);
}

export type HostKind = "apex" | "dashboard-slug" | "tenant" | "share" | "unknown";

/**
 * Classify a request host against the base domain.
 *
 * - "apex"           — the base domain itself. Tenantless only when it resolves no tenant.
 * - "dashboard-slug" — a reserved dashboard slug: always serves the cross-tenant dashboard.
 * - "tenant"         — {slug}.{base-domain}: serves that tenant's app. `slug` is set.
 * - "share"          — {token}.share.{base-domain}: the anonymous read-only view.
 * - "unknown"        — an unrelated host, or no base domain configured.
 */
export function classifyHost(
  host: string | null | undefined,
  baseDomain: string | null | undefined,
  dashboardSlugs: readonly string[] = []
): { kind: HostKind; slug: string | null } {
  if (!host || !baseDomain) return { kind: "unknown", slug: null };

  const hostname = host.split(":")[0]!;
  const baseHostname = baseDomain.split(":")[0]!;
  if (!baseHostname) return { kind: "unknown", slug: null };

  if (hostname.toLowerCase() === baseHostname.toLowerCase()) {
    return { kind: "apex", slug: null };
  }

  // Checked before the slug split: a share host's "{token}.share" is not a tenant slug, and
  // must never be mistaken for a reserved dashboard slug either.
  if (isShareHost(host)) return { kind: "share", slug: null };

  const slug = extractTenantSlug(host, baseDomain);
  if (!slug) return { kind: "unknown", slug: null };

  if (dashboardSlugs.includes(slug.toLowerCase())) {
    return { kind: "dashboard-slug", slug: null };
  }

  return { kind: "tenant", slug };
}

/**
 * Whether the host serves the cross-tenant dashboard rather than a single tenant's app.
 *
 * A reserved dashboard slug always does — that is what the operator reserved it for. The apex
 * does so only when nothing resolved behind it: a single-tenant install auto-resolves its sole
 * tenant there and must keep serving the full app, or its owner is trimmed to a dashboard and
 * then bounced to a wildcard subdomain that may not even have a certificate.
 */
export function isTenantlessHost(kind: HostKind, apexResolvesTenant: boolean): boolean {
  if (kind === "dashboard-slug") return true;
  if (kind === "apex") return !apexResolvesTenant;
  return false;
}
