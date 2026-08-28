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
