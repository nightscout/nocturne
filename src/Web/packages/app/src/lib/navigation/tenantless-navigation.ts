/**
 * Navigation filtering for tenantless hosts (the apex and reserved dashboard slugs).
 *
 * Almost every page in the app reads or writes one tenant's data, and on a tenantless host there
 * is no tenant for the API to resolve — those pages would render a shell and then 404. The
 * tenantless surface is therefore deliberately minimal: the cross-tenant dashboard, plus the
 * settings that belong to the signed-in subject rather than to a tenant.
 */

/** Hrefs that are meaningful without a resolved tenant. */
export const TENANTLESS_NAV_HREFS: readonly string[] = [
  "/", // the cross-tenant overview
  "/settings/account", // the subject's own credentials and profile
  "/settings/appearance", // display preferences, stored per subject
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
