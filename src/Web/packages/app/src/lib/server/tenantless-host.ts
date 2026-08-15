/**
 * Tenantless-host classification.
 *
 * Most hosts name a tenant ({slug}.{base-domain}). Two do not, and either may serve the
 * cross-tenant caregiver dashboard instead of a single tenant's app:
 *
 * - a reserved dashboard slug (dashboard.{base-domain}, app.{base-domain}), for hosted
 *   deployments whose apex is already taken by a marketing site — always the dashboard;
 * - the apex (the base domain itself) — the dashboard only when it resolves no tenant.
 *   The API auto-resolves a sole tenant on the apex, which is how a single-tenant
 *   self-hosted install serves the whole app at https://nocturne.example.com/, so the
 *   hostname alone cannot decide this one. See apexResolvedTenant.
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
  dashboardSlugs: readonly string[] = DEFAULT_DASHBOARD_SLUGS
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

/**
 * Whether the API resolved a tenant for this request, which on the apex means a sole tenant was
 * auto-resolved. Reported by the status endpoint because only the API can answer it: it is a
 * question about how many tenants exist, not about the hostname.
 *
 * An unreachable or erroring status call answers "no tenant", which serves the dashboard. That is
 * the safe direction — the dashboard renders for any signed-in subject, whereas the tenant app
 * would render a shell over an API that resolves nothing.
 */
export async function apexResolvedTenant(
  getStatus: () => Promise<{ tenantSlug?: string | null } | null | undefined>
): Promise<boolean> {
  try {
    const status = await getStatus();
    return !!status?.tenantSlug;
  } catch {
    return false;
  }
}
