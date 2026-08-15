/**
 * Tenant hostname helpers.
 *
 * Tenants are addressed as subdomains of a shared base domain
 * (`{slug}.{base-domain}`). The base domain itself comes from the server
 * (BASE_DOMAIN, via the root layout) and already carries any non-default port,
 * so it must never be re-derived or re-decorated on the client.
 */

/** Build the root URL for a tenant subdomain. */
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
 * Where a tenantless host should send a signed-in visitor.
 *
 * A caregiver with access to exactly one tenant has nothing to choose between, so the dashboard
 * would be a single tile in front of the app they actually want: send them straight to it.
 * Returns null — meaning "render the dashboard" — for zero or several tenants, or when no base
 * domain is configured and so no tenant URL can be built.
 */
export function resolveSingleTenantLanding(
  tenants: readonly { slug?: string | null }[] | null | undefined,
  baseDomain: string | null | undefined,
  protocol?: string
): string | null {
  if (!baseDomain) return null;

  const slugs = (tenants ?? []).map((t) => t.slug).filter((s): s is string => !!s);
  if (slugs.length !== 1) return null;

  return tenantUrl(slugs[0]!, baseDomain, protocol);
}
