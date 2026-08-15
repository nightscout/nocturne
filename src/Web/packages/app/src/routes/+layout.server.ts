import type { LayoutServerLoad } from "./$types";
import { getOriginalHost } from "$lib/server/request-host";
import { classifyHost, parseDashboardSlugs } from "$lib/server/tenantless-host";
import {
  PREFS_COOKIE_NAME,
  hasStoredPreferences,
  parsePrefsCookie,
} from "$lib/stores/appearance-store.svelte";

/**
 * Root layout server load function.
 * Provides session data to all routes.
 * Auth gating is handled by route group layouts.
 * Setup/recovery mode detection is in hooks.server.ts.
 */
export const load: LayoutServerLoad = async ({ locals, request, cookies }) => {
  // Tenant identity is resolved here, from the request host against BASE_DOMAIN,
  // so the browser never has to guess it by counting hostname labels. A share
  // host carries a token rather than a slug, so it has no tenant to name, and a
  // tenantless host (apex or a reserved dashboard slug) names no single tenant either.
  const host = getOriginalHost(request);
  const baseDomain = process.env.BASE_DOMAIN ?? null;
  const { kind, slug } = classifyHost(
    host,
    baseDomain,
    parseDashboardSlugs(process.env.DASHBOARD_SLUGS)
  );
  const tenantSlug = slug;
  const tenantless = kind === "tenantless";

  // Display preferences for SSR, in the same precedence the browser applies them
  // (backend blob over the mirrored cookie) so the markup matches hydration.
  const serverPrefs = locals.isAuthenticated ? locals.user?.preferences : null;
  const cookiePrefs = parsePrefsCookie(cookies.get(PREFS_COOKIE_NAME));
  const displayPreferences = [
    hasStoredPreferences(serverPrefs) ? serverPrefs : null,
    cookiePrefs,
  ].filter((prefs) => prefs !== null && prefs !== undefined);

  return {
    displayPreferences,
    user: locals.user,
    isAuthenticated: locals.isAuthenticated,
    effectivePermissions: locals.effectivePermissions ?? [],
    isPlatformAdmin: locals.isPlatformAdmin,
    isPlatformAccessGrant: locals.isPlatformAccessGrant ?? false,
    tenantSlug,
    tenantless,
    baseDomain,
  };
};
