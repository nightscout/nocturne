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
