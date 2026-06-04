/**
 * Helpers for recovering the original client-facing host and protocol from a
 * request behind the YARP gateway, and for resolving the effective tenant host.
 *
 * Shared by hooks.server.ts (proxying / session validation) and any server
 * route that needs to talk to the backend API with correct tenant resolution
 * (e.g. the realtime handshake-ticket endpoint).
 */

/**
 * Get the original client-facing host from the request.
 * YARP suppresses the original Host header when transforms are configured,
 * replacing it with the internal destination host. The original host is
 * preserved in X-Forwarded-Host, which we must read first.
 */
export function getOriginalHost(request: Request): string | null {
  return request.headers.get("x-forwarded-host") ?? request.headers.get("host");
}

/**
 * Get the original client-facing protocol from the request.
 * When behind a TLS-terminating reverse proxy (YARP), the internal request
 * is plain HTTP but X-Forwarded-Proto carries the original scheme. We must
 * forward this to internal API calls so the API's HTTPS enforcement
 * middleware treats them as secure.
 */
export function getOriginalProto(request: Request): string {
  return request.headers.get("x-forwarded-proto") ?? (request.url.startsWith("https") ? "https" : "http");
}

/**
 * Cookie set during setup to carry the tenant slug while the user is still
 * on the apex domain. httpOnly, 1-hour TTL, cleaned up by markSetupComplete.
 * Read by hooks that create API clients so they can prepend the slug to
 * X-Forwarded-Host for correct tenant resolution.
 */
export const SETUP_TENANT_COOKIE = "nocturne-setup-tenant";

/**
 * Returns the effective host for API calls, prepending the setup tenant slug
 * when available so the apex domain resolves to the correct tenant.
 */
export function getEffectiveHost(
  request: Request,
  cookies: { get(name: string): string | undefined },
): string | null {
  const host = getOriginalHost(request);
  const slug = cookies.get(SETUP_TENANT_COOKIE);
  if (slug && host && !host.startsWith(`${slug}.`)) return `${slug}.${host}`;
  return host;
}
