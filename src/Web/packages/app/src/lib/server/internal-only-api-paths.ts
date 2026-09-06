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
 * Matched the way ASP.NET routing would rather than by string equality. Routing percent-decodes,
 * is case-insensitive, and ignores trailing slashes, so `/api/v4/platform/%74ls-authorize`,
 * `/API/v4/platform/tls-authorize` and `/api/v4/platform/tls-authorize/` all reach the same endpoint
 * and must all be refused.
 *
 * A decoded `%2F` is rejoined as a separator here, which Kestrel does not do — it 404s on an
 * encoded slash. The mismatch only ever refuses a path the API would not serve, so it errs the
 * safe way.
 *
 * `path` must be an already-normalised pathname such as `URL.pathname`, which has had its dot
 * segments removed. This does not remove them, and Kestrel resolves `..` after decoding.
 */
export function isInternalOnlyApiPath(path: string): boolean {
  const normalized = path
    .split("/")
    .map(decodeSegment)
    .join("/")
    .toLowerCase()
    .replace(/\/+$/, "");

  return INTERNAL_ONLY_API_PATHS.includes(normalized);
}

/** A malformed escape is left as written; it cannot then match a listed path. */
function decodeSegment(segment: string): string {
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}
