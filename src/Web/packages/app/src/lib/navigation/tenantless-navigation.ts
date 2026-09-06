/**
 * Navigation filtering for tenantless hosts (the apex and reserved dashboard slugs).
 *
 * Almost every page in the app reads or writes one tenant's data, and on a tenantless host there
 * is no tenant for the API to resolve — those pages would render a shell and then 404. The
 * tenantless surface is therefore whatever the API admits without a tenant.
 */

/**
 * Hrefs that are meaningful without a resolved tenant.
 *
 * Anything added here needs every endpoint the page renders from admitted in
 * TenantResolutionMiddleware.TenantlessAllowedPaths first, or the nav gains an entry that 404s.
 */
export const TENANTLESS_NAV_HREFS: readonly string[] = [
  "/", // the cross-tenant overview
  // Subject-scoped half only (subjects.preferences / subjects.preferred_language); the page hides
  // its tenant-scoped half here.
  "/settings/appearance",
  // Passkeys, authenticators, linked identities, sessions and avatar — all keyed on the subject,
  // none carrying a tenant column.
  "/settings/account",
];

interface NavLike {
  href?: string;
  children?: NavLike[];
}

/**
 * Keep only the navigation reachable on a tenantless host. A parent whose children are all
 * tenant-scoped is dropped along with them, so no empty group is left behind.
 */
export function filterTenantlessNav<T extends NavLike>(items: readonly T[]): T[] {
  const allowed = new Set(TENANTLESS_NAV_HREFS);

  return items
    .map((item) => {
      if (!item.children) return item;
      const children = item.children.filter((c) => c.href && allowed.has(c.href));
      return { ...item, children } as T;
    })
    .filter((item) =>
      item.children ? item.children.length > 0 : !!item.href && allowed.has(item.href)
    );
}

/** Whether a pathname is reachable on a tenantless host. */
export function isTenantlessRoute(pathname: string): boolean {
  return TENANTLESS_NAV_HREFS.includes(pathname);
}
