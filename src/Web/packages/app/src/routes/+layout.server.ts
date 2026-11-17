import type { LayoutServerLoad } from "./$types";
import type { UserDisplayPreferences } from "#lib/api/index.js";
import { resolvePermissions } from "#lib/server/resolve-permissions.js";
import { displayPreferenceLayers, displayPreferenceSources } from "#lib/server/display-preference-layers.js";
import { classifyRequestHost, isTenantlessHost } from "#lib/server/tenantless-host.js";
import { getRequestStatus } from "#lib/server/request-status.js";
import { AUTH_COOKIE_NAMES } from "#lib/config/auth-cookies.js";
import { SHELL_DATA_KEY } from "#lib/constants/shell-data.js";
import { parseLastSignIn } from "#lib/components/auth/last-sign-in.js";
import {
  LANGUAGE_COOKIE_NAME,
  resolveLanguage,
} from "#lib/stores/appearance-store.svelte.js";

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

  // Together with the scopes because on a share host both are API round-trips and neither
  // reads the other.
  const [tenantPresentation, permissions] = await Promise.all([
    resolveTenantPresentation(locals),
    resolvePermissions(locals),
  ]);
  const sources = displayPreferenceSources(locals, cookies, tenantPresentation);
  const displayPreferences = displayPreferenceLayers(sources);
  const displayLanguage = resolveLanguage(
    locals.isAuthenticated ? locals.user?.preferredLanguage : null,
    cookies.get(LANGUAGE_COOKIE_NAME)
  );

  const hasAccount = locals.isAuthenticated && !locals.isGuestSession;

  return {
    displayPreferences,
    displayLanguage,
    serverPreferences: sources.serverPrefs,
    tenantPreferences: sources.tenantPreferences,
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
