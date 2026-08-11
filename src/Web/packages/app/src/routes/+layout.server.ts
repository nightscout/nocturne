import type { LayoutServerLoad } from "./$types";
import { extractTenantSlug, getOriginalHost, isShareHost } from "$lib/server/request-host";

/**
 * Root layout server load function.
 * Provides session data to all routes.
 * Auth gating is handled by route group layouts.
 * Setup/recovery mode detection is in hooks.server.ts.
 */
export const load: LayoutServerLoad = async ({ locals, request }) => {
  // Tenant identity is resolved here, from the request host against BASE_DOMAIN,
  // so the browser never has to guess it by counting hostname labels. A share
  // host carries a token rather than a slug, so it has no tenant to name.
  const host = getOriginalHost(request);
  const baseDomain = process.env.BASE_DOMAIN ?? null;
  const tenantSlug = isShareHost(host) ? null : extractTenantSlug(host, baseDomain);

  return {
    user: locals.user,
    isAuthenticated: locals.isAuthenticated,
    effectivePermissions: locals.effectivePermissions ?? [],
    isPlatformAdmin: locals.isPlatformAdmin,
    isPlatformAccessGrant: locals.isPlatformAccessGrant ?? false,
    tenantSlug,
    baseDomain,
  };
};
