/**
 * True when the host is a public share link of the form {token}.share.{baseDomain}.
 * The bare {slug}.{baseDomain} host and the literal share.{baseDomain} label are not share hosts.
 * Mirrors the API's TenantResolutionMiddleware, which gates anonymous read on this host shape.
 *
 * Pure host-string predicate with no server-only dependencies, so it is safe to import on the
 * client (e.g. the 401 auth-interceptor) as well as in server hooks.
 */
export function isShareHost(host: string | null | undefined): boolean {
  return host != null && /^[^.]+\.share\./i.test(host);
}

/**
 * Where a share host goes when its link serves nothing — rotated, disabled, or never valid.
 *
 * A share host holds no session and answers for one link only, so every other dead end the app
 * has (sign-in, the first-run wizard, the marketing site) either cannot be satisfied there or
 * reads as "this deployment is broken". Its page says only that the link is not working, which
 * is all a visitor can act on and all a stranger may learn.
 */
export const SHARE_UNAVAILABLE_PATH = "/share/unavailable";
