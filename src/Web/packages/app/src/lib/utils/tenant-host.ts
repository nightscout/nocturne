/**
 * Tenant addressing: which tenants a signed-in visitor can go to, and the URLs that take them there.
 *
 * Tenants are addressed as subdomains of a shared base domain
 * (`{slug}.{base-domain}`). The base domain itself comes from the server
 * (BASE_DOMAIN, via the root layout) and already carries any non-default port,
 * so it must never be re-derived or re-decorated on the client.
 */
import type { TenantDto } from "../api/generated/nocturne-api-client";

export function tenantUrl(
  slug: string,
  baseDomain: string,
  protocol: string = typeof window !== "undefined"
    ? window.location.protocol
    : "https:"
): string {
  return `${protocol}//${slug}.${baseDomain}/`;
}

/**
 * Sends the browser to `slug`'s app. Each tenant is its own origin, so this is a document
 * navigation rather than a client-side one.
 */
export function goToTenant(slug: string, baseDomain: string): void {
  window.location.href = tenantUrl(slug, baseDomain);
}

/**
 * The `Domain` attribute scoping a cookie to every host under the base domain, or null where a
 * browser would reject or discard it: a single-label host, an IP literal, or a `*.localhost` name
 * (Chromium does not reliably scope cookies across those).
 *
 * Mirrors the API's `SessionCookieExtensions.ResolveCookieDomain`. The two must agree, or a
 * browser holds its preferences at one scope and its session at another.
 */
export function resolveCookieDomain(baseDomain: string | null | undefined): string | null {
  // A value carrying a port is discarded wholesale.
  const host = (baseDomain ?? "").split(":")[0]!;
  if (!host) return null;
  // Any all-numeric dotted form, not just four octets: the API uses IPAddress.TryParse, which
  // also accepts shorthand such as "10.0.1".
  // eslint-disable-next-line security/detect-unsafe-regex -- linear: each repetition opens with a literal ".".
  if (/^\d+(\.\d+)*$/.test(host)) return null;
  if (!host.includes(".")) return null;
  if (host.toLowerCase().endsWith(".localhost")) return null;

  return `.${host}`;
}

/** A reachable tenant as the tenant list returns it. */
export type TenantListEntry = Pick<TenantDto, "id" | "slug" | "displayName">;

/**
 * Where a tenantless host should send a signed-in visitor.
 *
 * A caregiver with access to exactly one tenant has nothing to choose between, so the dashboard
 * would be a single tile in front of the app they actually want: send them straight to it.
 * Returns null — meaning "render the dashboard" — for zero or several tenants, or when no base
 * domain is configured and so no tenant URL can be built.
 *
 * It also returns null when the sole tenant's own slug is a reserved dashboard slug. Its host is
 * then the dashboard host, so redirecting there would land back on this same load and redirect
 * again, forever. Nothing is reserved by default, so this only arises once an operator sets
 * DASHBOARD_SLUGS — and it may name a slug some tenant already holds.
 */
export function resolveSingleTenantLanding(
  tenants: readonly TenantListEntry[] | null | undefined,
  baseDomain: string | null | undefined,
  protocol?: string,
  dashboardSlugs: readonly string[] = []
): string | null {
  if (!baseDomain) return null;

  const slugs = (tenants ?? [])
    .map((t) => t.slug)
    .filter((s): s is string => !!s);
  if (slugs.length !== 1) return null;

  const slug = slugs[0]!;
  if (dashboardSlugs.includes(slug.toLowerCase())) return null;

  return tenantUrl(slug, baseDomain, protocol);
}

/** One entry of the sidebar tenant switcher: a tenant this visitor can view. */
export interface TenantSwitcherTarget {
  slug: string;
  displayName: string | null;
}

/**
 * The sidebar tenant switcher's entries: the reachable tenants, including the one the visitor is
 * already on, so the switcher can name what is on screen from the same list it offers. A tenant
 * with no slug has no host to send anyone to.
 */
export function toTenantSwitcherTargets(
  tenants: readonly TenantListEntry[]
): TenantSwitcherTarget[] {
  return tenants
    .filter((t): t is TenantListEntry & { slug: string } => !!t.slug)
    .map((t) => ({ slug: t.slug, displayName: t.displayName ?? null }));
}
