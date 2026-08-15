/**
 * Navigation filtering for tenantless hosts (the apex and reserved dashboard slugs).
 *
 * Almost every page in the app reads or writes one tenant's data, and on a tenantless host there
 * is no tenant for the API to resolve — those pages would render a shell and then 404. The
 * tenantless surface is therefore whatever the API admits without a tenant, which today is the
 * cross-tenant overview and nothing else.
 *
 * The account and appearance settings pages look subject-scoped and are not: they call
 * /api/auth/passkey/*, /api/v4/totp/*, and /api/v4/settings, none of which the API serves off a
 * tenant. Listing them would put two entries in the sidebar that 404 when opened. Widening the
 * auth endpoints to tenantless hosts is a security-relevant change, and /api/v4/settings is
 * genuinely tenant-scoped, so they stay off the list until the API surface catches up.
 */

/** Hrefs that are meaningful without a resolved tenant. */
export const TENANTLESS_NAV_HREFS: readonly string[] = [
  "/", // the cross-tenant overview
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
