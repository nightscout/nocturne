import type { LayoutServerLoad } from "./$types";
import type { UserDisplayPreferences } from "$lib/api";
import { resolvePermissions } from "$lib/server/resolve-permissions";
import { classifyRequestHost, isTenantlessHost } from "$lib/server/tenantless-host";
import { getRequestStatus } from "$lib/server/request-status";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { SHELL_DATA_KEY } from "$lib/constants/shell-data";
import { parseLastSignIn } from "$lib/components/auth/last-sign-in";
import {
  LANGUAGE_COOKIE_NAME,
  PREFS_COOKIE_NAME,
  hasStoredPreferences,
  parsePrefsCookie,
  resolveLanguage,
} from "$lib/stores/appearance-store.svelte";

/**
 * The owner's presentation settings, for the viewers with no account of their own to save any: a
 * public share and a guest link. They should see the data the way its owner reads it (their
 * units, their clock, their colours) rather than the frontend's defaults. Only presentation
 * crosses; the owner's display language does not, because adopting it would rewrite the viewer's
 * own base-domain-wide language cookie. A refused call leaves the viewer on the defaults instead of
 * failing the page.
 */
async function resolveTenantPresentation(
  locals: App.Locals
): Promise<UserDisplayPreferences | null> {
  if (!locals.isShareHost && !locals.isGuestSession) return null;

  try {
    return await locals.apiClient.tenantAppearance.getTenantAppearance();
  } catch {
    return null;
  }
}

/**
 * Root layout server load function.
 * Provides session data to all routes.
 * Auth gating is handled by route group layouts.
 * Setup/recovery mode detection is in hooks.server.ts.
 */
export const load: LayoutServerLoad = async ({ locals, request, cookies, depends }) => {
  depends(SHELL_DATA_KEY);

  // Tenant identity is resolved here, from the request host against BASE_DOMAIN,
  // so the browser never has to guess it by counting hostname labels. A share
  // host carries a token rather than a slug, so it has no tenant to name, and a
  // reserved dashboard slug names no single tenant either.
  const {
    kind,
    slug: tenantSlug,
    baseDomain,
    dashboardSlugs,
  } = classifyRequestHost(request);

  // Resolved once here, for every route: the apex needs the API's answer (does a sole tenant
  // resolve behind it?) and asking per-page would repeat both the question and the round-trip.
  // Children read `tenantless` from this layout's data via parent().
  const tenantless = isTenantlessHost(
    kind,
    kind === "apex" ? Boolean((await getRequestStatus(locals))?.tenantSlug) : false
  );

  // Display preferences for SSR, in the same precedence the browser applies them so the markup
  // matches hydration: a share is drawn as its owner draws it, the viewer's cookie left out;
  // a member's backend blob wins over the cookie; a guest's own cookie wins field by field, and
  // the tenant's presentation stands in only for the defaults. Together with the scopes because
  // on a share host both are API round-trips and neither reads the other.
  const [tenantPresentation, permissions] = await Promise.all([
    resolveTenantPresentation(locals),
    resolvePermissions(locals),
  ]);
  const serverPrefs = locals.isShareHost
    ? tenantPresentation
    : locals.isAuthenticated
      ? (locals.user?.preferences ?? null)
      : null;
  const tenantPreferences = locals.isGuestSession ? tenantPresentation : null;
  const cookiePrefs = locals.isShareHost ? null : parsePrefsCookie(cookies.get(PREFS_COOKIE_NAME));
  const displayPreferences = [
    hasStoredPreferences(serverPrefs) ? serverPrefs : null,
    cookiePrefs,
    hasStoredPreferences(tenantPreferences) ? tenantPreferences : null,
  ].filter((prefs) => prefs !== null && prefs !== undefined);
  const displayLanguage = resolveLanguage(
    locals.isAuthenticated ? locals.user?.preferredLanguage : null,
    cookies.get(LANGUAGE_COOKIE_NAME)
  );

  const hasAccount = locals.isAuthenticated && !locals.isGuestSession;

  return {
    displayPreferences,
    displayLanguage,
    serverPreferences: serverPrefs,
    tenantPreferences,
    user: locals.user,
    isAuthenticated: locals.isAuthenticated,
    hasAccount,
    isShareHost: locals.isShareHost,
    effectivePermissions: permissions.scopes ?? [],
    limitTo24Hours: permissions.limitTo24Hours ?? false,
    refusedAsDemoSubject: permissions.refusedAsDemoSubject ?? false,
    isPlatformAdmin: locals.isPlatformAdmin,
    isPlatformAccessGrant: locals.isPlatformAccessGrant ?? false,
    tenantSlug,
    tenantless,
    baseDomain,
    dashboardSlugs,
    // Read here rather than from document.cookie so the login form renders its "Last used" badge
    // in the server markup, and the buttons don't reorder under the visitor on hydration.
    lastSignIn: parseLastSignIn(cookies.get(AUTH_COOKIE_NAMES.lastSignIn)),
  };
};
