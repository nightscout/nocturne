/**
 * API paths the proxy refuses, making them reachable only from inside the deployment network.
 *
 * `/api/v4/platform/tls-authorize` is Caddy's `on_demand_tls` "ask" hook. Caddy calls the API
 * container directly, never through this proxy, so refusing it here costs nothing; proxying it would
 * expose an anonymous "is this tenant slug active?" oracle (200 vs 404) to the internet. The hook
 * cannot be header-authenticated — Caddy's `ask` takes only a URL and sends no custom headers.
 */
const INTERNAL_ONLY_API_PATHS = ["/api/v4/platform/tls-authorize"];

/**
 * Matched the way ASP.NET routing would rather than by string equality: routing is
 * case-insensitive and ignores trailing slashes, so `/API/v4/platform/tls-authorize` and
 * `/api/v4/platform/tls-authorize/` reach the same endpoint and must be refused too.
 */
export function isInternalOnlyApiPath(path: string): boolean {
  const normalized = path.toLowerCase().replace(/\/+$/, "");
  return INTERNAL_ONLY_API_PATHS.includes(normalized);
}
