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
