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
