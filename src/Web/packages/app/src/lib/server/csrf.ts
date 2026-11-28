const MUTATING_METHODS = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/** The content types an HTML form can send, and SvelteKit's own binary form encoding. */
const FORM_CONTENT_TYPES = new Set([
  "application/x-www-form-urlencoded",
  "multipart/form-data",
  "text/plain",
  "application/x-sveltekit-formdata",
]);

const WEBHOOK_PREFIX = "/api/v4/webhooks";
const WEB_APP_API_PREFIXES = [WEBHOOK_PREFIX, "/api/v4/bot", "/api/otel"];

function isUnderPath(pathname: string, prefix: string): boolean {
  return pathname === prefix || pathname.startsWith(`${prefix}/`);
}

/** The /api paths that are SvelteKit routes of the web app itself, not the API's. */
export function isWebAppApiPath(pathname: string): boolean {
  return WEB_APP_API_PREFIXES.some((prefix) => isUnderPath(pathname, prefix));
}

/** Paths the /api proxy forwards to the API. */
export function isProxiedApiPath(pathname: string): boolean {
  return pathname.startsWith("/api") && !isWebAppApiPath(pathname);
}

/**
 * The chat platforms' webhooks. A browser can forge a form post to one, but not the request
 * signature or secret each platform's adapter verifies before any handler runs, and the route
 * acts on no cookie of the caller.
 */
export function isSignedWebhookPath(pathname: string): boolean {
  return isUnderPath(pathname, WEBHOOK_PREFIX);
}

/**
 * Whether a request is a cross-site form submission to refuse. This is SvelteKit's own origin
 * check, which `csrf.trustedOrigins: ["*"]` turns off so this one can run in its place, with two
 * differences: on a proxied /api path a PUT, PATCH or DELETE with no Content-Type passes, and any
 * request to a {@link isSignedWebhookPath} passes, because Slack posts forms with no Origin.
 *
 * SvelteKit counts a request with no Content-Type as a form, so it refuses the bodyless DELETE
 * that AndroidAPS (NSClientV3) and other uploaders send to `/api/v3/{collection}/{id}`. No
 * browser can forge one: a cross-site PUT, PATCH or DELETE needs a CORS preflight, and no HTML
 * form sends those methods. A bodyless POST needs no preflight, so it stays refused.
 *
 * Remote function requests have their own check in SvelteKit, which this setting does not change.
 *
 * Like SvelteKit, it reads the raw request URL, before `__data.json` and route suffixes are
 * stripped. SvelteKit's self origin is `kit.paths.origin` when set; nothing sets it, so the request
 * URL's origin is the same.
 */
export function isCrossSiteFormRequest(request: Request): boolean {
  if (!MUTATING_METHODS.has(request.method)) return false;
  const url = new URL(request.url);
  if (request.headers.get("origin") === url.origin || isSignedWebhookPath(url.pathname)) return false;

  const contentType = request.headers.get("content-type");
  if (contentType === null || contentType === "") {
    return !(isProxiedApiPath(url.pathname) && request.method !== "POST");
  }

  const mediaType = contentType.split(";", 1)[0].trim().toLowerCase();
  return FORM_CONTENT_TYPES.has(mediaType);
}
