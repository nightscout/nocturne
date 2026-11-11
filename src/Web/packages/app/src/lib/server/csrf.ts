const MUTATING_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/** The content types an HTML form can send, and SvelteKit's own binary form encoding. */
const FORM_CONTENT_TYPES = new Set([
  "application/x-www-form-urlencoded",
  "multipart/form-data",
  "text/plain",
  "application/x-sveltekit-formdata",
]);

/**
 * Paths the /api proxy forwards to the API. The excluded prefixes are SvelteKit routes of the
 * web app itself.
 */
export function isProxiedApiPath(pathname: string): boolean {
  return (
    pathname.startsWith("/api") &&
    !pathname.startsWith("/api/v4/webhooks") &&
    !pathname.startsWith("/api/v4/bot") &&
    !pathname.startsWith("/api/otel")
  );
}

/**
 * Whether a request is a cross-site form submission to refuse. This is SvelteKit's own origin
 * check, which `csrf.trustedOrigins: ["*"]` turns off so this one can run in its place, with one
 * difference: on a proxied /api path a PUT, PATCH or DELETE with no Content-Type passes.
 *
 * SvelteKit counts a request with no Content-Type as a form, so it refuses the bodyless DELETE
 * that AndroidAPS (NSClientV3) and other uploaders send to `/api/v3/{collection}/{id}`. No
 * browser can forge one: a cross-site PUT, PATCH or DELETE needs a CORS preflight, and no HTML
 * form sends those methods. A bodyless POST needs no preflight, so it stays refused.
 *
 * Remote function requests have their own check in SvelteKit, which this setting does not change.
 */
export function isCrossSiteFormRequest(request: Request, selfOrigin: string, pathname: string): boolean {
  if (!MUTATING_METHODS.has(request.method)) return false;
  if (request.headers.get("origin") === selfOrigin) return false;

  const contentType = request.headers.get("content-type");
  if (contentType === null || contentType === "") {
    return !(isProxiedApiPath(pathname) && request.method !== "POST");
  }

  const mediaType = contentType.split(";", 1)[0].trim().toLowerCase();
  return FORM_CONTENT_TYPES.has(mediaType);
}
