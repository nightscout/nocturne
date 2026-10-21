/**
 * The public glucose clock face is meant to be framed: the desktop companion hosts
 * `/clock/{id}?embed=1` in an iframe of its own window. The pages beneath it are not.
 */
const FRAMEABLE_PATH = /^\/clock\/[^/]+\/?$/;

const SCHEME = /^[a-z][a-z0-9+.-]*$/i;
const HOST = /^[a-z0-9*.-]+$/i;
const PORT = /^(\d+|\*)$/;

/** A CSP host or scheme source (`https://ha.example.com:8123`, `*.example.com`, `https:`). */
function isFrameAncestor(source: string): boolean {
  if (source.endsWith(":") && SCHEME.test(source.slice(0, -1))) return true;
  const separator = source.indexOf("://");
  if (separator !== -1 && !SCHEME.test(source.slice(0, separator))) return false;
  const [host, port, ...rest] = source.slice(separator === -1 ? 0 : separator + 3).split(":");
  return rest.length === 0 && HOST.test(host) && (port === undefined || PORT.test(port));
}

/**
 * Reads `ALLOWED_FRAME_ANCESTORS`: whitespace- or comma-separated sources allowed to frame the
 * signed-in app (e.g. a Home Assistant dashboard). Anything that is not a plain host or scheme
 * source is dropped, so the setting cannot smuggle other directives into the header.
 */
export function parseFrameAncestors(raw: string | undefined): string[] {
  return (raw ?? "").split(/[\s,]+/).filter(isFrameAncestor);
}

/**
 * Response headers every reply from the web app carries, including the /api responses it proxies.
 *
 * A public share host sends no referrer at all, keeping the token-bearing URL out of Referer
 * headers, and asks not to be indexed. Its pages are anonymous and read-only and are embedded on
 * purpose (iframe dashboard cards, mirrors), so like the clock face anyone may frame them.
 * Everything else may be framed by itself and the configured frame ancestors, or by nothing when
 * none are configured.
 */
export function applySecurityHeaders(
  headers: Headers,
  {
    isShareHost,
    pathname,
    frameAncestors = [],
  }: { isShareHost: boolean; pathname: string; frameAncestors?: string[] },
): void {
  headers.set("X-Content-Type-Options", "nosniff");

  if (isShareHost) {
    headers.set("Referrer-Policy", "no-referrer");
    headers.set("X-Robots-Tag", "noindex, nofollow");
  } else if (!headers.has("Referrer-Policy")) {
    headers.set("Referrer-Policy", "strict-origin-when-cross-origin");
  }

  // A proxied API response arrives with the API's own X-Frame-Options; framing is decided here.
  headers.delete("X-Frame-Options");
  if (isShareHost || FRAMEABLE_PATH.test(pathname)) {
    return;
  }
  if (frameAncestors.length > 0) {
    headers.set("Content-Security-Policy", `frame-ancestors 'self' ${frameAncestors.join(" ")}`);
  } else {
    headers.set("X-Frame-Options", "DENY");
  }
}
