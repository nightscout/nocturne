import { env } from "$env/dynamic/private";
import { safeReturnUrl } from "$lib/server/return-url";

// Marker appended to returnUrl so a single auto-login attempt can be detected
// after it bounces back. It survives the round-trip because the auth guard
// rebuilds returnUrl from pathname + search.
export const AUTO_LOGIN_MARKER = "__autologin";

// Auto-login endpoints. Both issue a real session for a tenant member, set the
// normal cookies, and bounce back to the redirect target.
//
// Dev auto-login: with NOCTURNE_DEV_AUTO_LOGIN=true (forwarded from the host
// environment by the Aspire AppHost, run mode only), sign in as the tenant's
// first owner instead of rendering the passkey UI. The endpoint exists only when
// the API runs in Development; elsewhere the redirect lands on a 404, never a
// session.
//
// Demo sign-in: a demo tenant has no owner credentials and exists to be explored,
// so sign every visitor in as its shared demo member. The endpoint responds only
// on a tenant whose IsDemo flag is set; any other tenant gets a 404.
export const DEV_LOGIN_ENDPOINT = "/api/v4/dev-only/auth/login";
export const DEMO_LOGIN_ENDPOINT = "/api/v4/demo/session";

/**
 * The auto-login endpoint for a request, or null when sign-in should render the
 * normal passkey UI. The share host serves the anonymous read-only view and never
 * honors credentials, so there is no session to be had there.
 */
export function autoLoginEndpoint(
  isDemo: boolean | undefined,
  isShareHost: boolean | undefined,
): string | null {
  if (env.NOCTURNE_DEV_AUTO_LOGIN === "true") return DEV_LOGIN_ENDPOINT;
  if (isShareHost) return null;
  return isDemo ? DEMO_LOGIN_ENDPOINT : null;
}

/**
 * Where to send a visitor for their one auto-login attempt, or null once it has
 * been spent.
 *
 * The session is host-scoped (cookie domain = the exact host that set it) and only
 * authenticates on a tenant subdomain. Opened on the apex host, the issued session
 * never authenticates, so the auth guard bounces straight back — and a blind
 * re-redirect would loop forever, flooding the API with 401s from the dashboard load
 * on every pass. If the marker is already present the attempt has been had, and the
 * caller falls through to the passkey UI instead of retrying.
 */
export function autoLoginRedirect(
  endpoint: string,
  returnUrl: string,
  origin: string,
): string | null {
  if (new URL(returnUrl, origin).searchParams.has(AUTO_LOGIN_MARKER)) return null;
  return `${endpoint}?redirect=${encodeURIComponent(withMarker(returnUrl, origin, true))}`;
}

/**
 * `returnUrl` with the marker set or removed. Rebuilding from the parsed URL
 * resolves dot segments, so the result is checked again rather than trusted.
 */
export function withMarker(returnUrl: string, origin: string, marked: boolean): string {
  const target = new URL(returnUrl, origin);
  if (marked) target.searchParams.set(AUTO_LOGIN_MARKER, "1");
  else target.searchParams.delete(AUTO_LOGIN_MARKER);
  return safeReturnUrl(target.pathname + target.search);
}
