import type { LayoutServerLoad } from "./$types";

/**
 * Root layout server load function.
 * Provides session data to all routes.
 * Auth gating is handled by route group layouts.
 * Setup/recovery mode detection is in hooks.server.ts.
 */
export const load: LayoutServerLoad = async ({ locals }) => {
  return {
    user: locals.user,
    isAuthenticated: locals.isAuthenticated,
    effectivePermissions: locals.effectivePermissions ?? [],
    isPlatformAdmin: locals.isPlatformAdmin,
    isPlatformAccessGrant: locals.isPlatformAccessGrant ?? false,
  };
};
