import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";

/**
 * Route classification shared by the request hooks.
 */

/** Static asset paths that bypass all middleware. */
export const STATIC_ASSET_PREFIXES = ["/_app", "/assets", "/favicon.ico"] as const;

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
