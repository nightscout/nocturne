import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";

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

/** What the request hooks know about a status probe that failed. */
export interface StatusProbeFailure {
  /** Whether the request arrived on a public share host ({token}.share.{base-domain}). */
  isShareHost: boolean;
  /** The HTTP status the API answered with, if it answered at all. */
  apiStatus: unknown;
  /** Whether the API's 503 body declared recovery mode. */
  recoveryMode: boolean;
  /** The marketing site to land an unresolvable non-share host on, if one is configured. */
  marketingUrl: string | undefined;
}

/**
 * Where a failed status probe sends the request, or null to let it render and decide for itself.
 *
 * A share host answers for one tenant's link and nothing else, so it is claimed ahead of every
 * instance-wide destination below: it holds no session for the recovery sign-in, the marketing site
 * belongs to a different domain, and /setup offers a first-run wizard on an instance a share link
 * only resolves on once it is past setup. The three statuses it claims mean an unresolvable token
 * (404), a suspended tenant (403) and an API that is itself unready (503) — the page it lands on
 * names none of them, only that the link is not working, because from here they are indistinguishable
 * to the visitor and only one of them is even permanent.
 */
export function statusProbeRedirect({
  isShareHost,
  apiStatus,
  recoveryMode,
  marketingUrl,
}: StatusProbeFailure): { location: string; status: 302 | 303 } | null {
  if (isShareHost && (apiStatus === 404 || apiStatus === 403 || apiStatus === 503)) {
    return { location: SHARE_UNAVAILABLE_PATH, status: 303 };
  }

  // Any 503 (setup_required, no tenants, or an unparseable body) means the instance isn't ready.
  if (apiStatus === 503) {
    return { location: recoveryMode ? "/auth/recovery" : "/setup", status: 303 };
  }

  // No tenant for this subdomain, or an apex with no tenants set up yet. A configured marketing
  // site is the SaaS apex landing; without one this is likely a self-hosted install that has yet
  // to create its first tenant.
  if (apiStatus === 404) {
    return marketingUrl
      ? { location: marketingUrl, status: 302 }
      : { location: "/setup", status: 303 };
  }

  return null;
}
