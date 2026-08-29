/**
 * Route classification shared by the request hooks.
 *
 * `PUBLIC_PREFIXES` must stay in step with every link the API emits to someone
 * who does not yet have a session — an invitee following `{baseUrl}/join?token=…`
 * (MemberInviteService) has no account, so gating `/join` behind
 * requireAuthentication makes the invite impossible to accept.
 */

/** Static asset paths that bypass all middleware. */
export const STATIC_ASSET_PREFIXES = ["/_app", "/assets", "/favicon.ico"] as const;

/** Route prefixes that bypass requireAuthentication enforcement. */
export const PUBLIC_PREFIXES = [
  "/auth",
  "/api",
  "/setup",
  "/clock",
  "/join",
  "/terms",
  "/privacy",
  "/guest",
] as const;

export function isPublicRoute(pathname: string): boolean {
  return (
    pathname === "/" ||
    PUBLIC_PREFIXES.some((p) => pathname.startsWith(p)) ||
    STATIC_ASSET_PREFIXES.some((p) => pathname.startsWith(p))
  );
}

/** The request facts the site-wide requireAuthentication gate decides on. */
export interface SignInGate {
  pathname: string;
  /** The tenant's site-level requireAuthentication setting. */
  requireAuthentication: boolean;
  isAuthenticated: boolean;
  /** Whether the request arrived on a public share host ({token}.share.{base-domain}). */
  isShareHost: boolean;
}

/**
 * Whether the site-wide requireAuthentication gate should send this request to sign-in.
 *
 * A share host is exempt from the gate wholesale, not route by route: it never carries a session,
 * because the auth handler leaves its cookies unread (see authHandle), so `isAuthenticated` is
 * false there for the tenant's owner and for a stranger alike. Asking a host that cannot hold a
 * session whether it holds one can only ever redirect, and the sign-in page it lands on is for an
 * account the host would not honor either. The API applies the same setting itself and grants a
 * share host only the public read the tenant published.
 */
export function requiresSignIn({
  pathname,
  requireAuthentication,
  isAuthenticated,
  isShareHost,
}: SignInGate): boolean {
  if (isShareHost) return false;
  if (isPublicRoute(pathname)) return false;
  return requireAuthentication && !isAuthenticated;
}

/**
 * Whether a failed status probe means the share link itself is the dead end, rather than the
 * instance-wide condition the same status means on any other host.
 *
 * A share host answers for one tenant's link and nothing else, so the destinations those statuses
 * otherwise carry are all wrong for it: a marketing site on another domain, a first-run wizard on
 * an instance a share link only resolves on once it is past setup, a recovery sign-in the host
 * holds no session for. The API 404s a token it cannot resolve — rotated, disabled, or never
 * valid — and 503s only when it is itself unready; either way the link is what the visitor came
 * for and is not working.
 */
export function shareLinkIsDeadEnd(isShareHost: boolean, apiStatus: unknown): boolean {
  return isShareHost && (apiStatus === 404 || apiStatus === 503);
}
