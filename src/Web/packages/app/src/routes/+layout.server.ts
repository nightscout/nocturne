import type { LayoutServerLoad } from "./$types";
import type { UserDisplayPreferences } from "$lib/api";
import { getOriginalHost } from "$lib/server/request-host";
import {
  classifyHost,
  isTenantlessHost,
  parseDashboardSlugs,
} from "$lib/server/tenantless-host";
import { getRequestStatus } from "$lib/server/request-status";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { parseLastSignIn } from "$lib/components/auth/last-sign-in";
import {
  LANGUAGE_COOKIE_NAME,
  PREFS_COOKIE_NAME,
  hasStoredPreferences,
  parsePrefsCookie,
  resolveLanguage,
} from "$lib/stores/appearance-store.svelte";

/**
 * The viewer's granted scopes, which the UI uses to offer only what the viewer can load.
 * `authHandle` resolves them for a signed-in member; a public share link and a guest link never
 * reach that branch, so their grant — the share's shareable read categories, or the scopes on
 * the guest grant — is resolved here instead. Failure leaves the viewer with nothing rather
 * than an over-offer.
 */
async function resolveEffectivePermissions(locals: App.Locals): Promise<string[]> {
  if (locals.effectivePermissions) return locals.effectivePermissions;
  if (!locals.isShareHost && !locals.isGuestSession) return [];

  try {
    return await locals.apiClient.myPermissions.getMyPermissions();
  } catch {
    return [];
  }
}

/**
 * The saved display preferences this viewer's page is rendered with. A signed-in member has
 * their own. A public share viewer has no account to have any, so the link owner's presentation
 * settings stand in: a share link should show its recipient the data the way its sender reads
 * it — their units, their clock, their colours — rather than the frontend's defaults. Only
 * presentation crosses; the owner's display language does not, because adopting it would rewrite
 * the viewer's own base-domain-wide language cookie. A refused call leaves the viewer on the
 * defaults instead of failing the page.
 */
async function resolveServerPreferences(
  locals: App.Locals
): Promise<UserDisplayPreferences | null> {
  if (locals.isAuthenticated) return locals.user?.preferences ?? null;
  if (!locals.isShareHost) return null;

  try {
    return await locals.apiClient.shareAppearance.getShareAppearance();
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
export const load: LayoutServerLoad = async ({ locals, request, cookies }) => {
  // Tenant identity is resolved here, from the request host against BASE_DOMAIN,
  // so the browser never has to guess it by counting hostname labels. A share
  // host carries a token rather than a slug, so it has no tenant to name, and a
  // reserved dashboard slug names no single tenant either.
  const host = getOriginalHost(request);
  const baseDomain = process.env.BASE_DOMAIN ?? null;
  const dashboardSlugs = parseDashboardSlugs(process.env.DASHBOARD_SLUGS);
  const { kind, slug } = classifyHost(host, baseDomain, dashboardSlugs);
  const tenantSlug = slug;

  // Resolved once here, for every route: the apex needs the API's answer (does a sole tenant
  // resolve behind it?) and asking per-page would repeat both the question and the round-trip.
  // Children read `tenantless` from this layout's data via parent().
  const tenantless = isTenantlessHost(
    kind,
    kind === "apex" ? Boolean((await getRequestStatus(locals))?.tenantSlug) : false
  );

  // Display preferences for SSR, in the same precedence the browser applies them
  // (backend blob over the mirrored cookie) so the markup matches hydration. Together with the
  // scopes because on a share host both are API round-trips and neither reads the other.
  const [serverPrefs, effectivePermissions] = await Promise.all([
    resolveServerPreferences(locals),
    resolveEffectivePermissions(locals),
  ]);
  const cookiePrefs = parsePrefsCookie(cookies.get(PREFS_COOKIE_NAME));
  const displayPreferences = [
    hasStoredPreferences(serverPrefs) ? serverPrefs : null,
    cookiePrefs,
  ].filter((prefs) => prefs !== null && prefs !== undefined);
  const displayLanguage = resolveLanguage(
    locals.isAuthenticated ? locals.user?.preferredLanguage : null,
    cookies.get(LANGUAGE_COOKIE_NAME)
  );

  return {
    displayPreferences,
    displayLanguage,
    serverPreferences: serverPrefs,
    user: locals.user,
    isAuthenticated: locals.isAuthenticated,
    isShareHost: locals.isShareHost,
    effectivePermissions,
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
