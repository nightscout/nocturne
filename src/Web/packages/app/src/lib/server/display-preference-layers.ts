import type { Cookies } from "@sveltejs/kit";
import type { UserDisplayPreferences } from "#lib/api/index.js";
import {
  PREFS_COOKIE_NAME,
  UNITS_COOKIE_NAME,
  hasStoredPreferences,
  parseDevicePreferences,
} from "#lib/stores/appearance-store.svelte.js";

export interface DisplayPreferenceSources {
  serverPrefs: UserDisplayPreferences | null;
  cookiePrefs: UserDisplayPreferences | null;
  tenantPreferences: UserDisplayPreferences | null;
  tenantDefault: string | null | undefined;
}

/**
 * Where each layer comes from for one request, as the browser applies them so server markup
 * matches hydration: a share is drawn as its owner draws it, the viewer's cookie left out; a
 * member's backend blob wins over the cookie; a guest's own cookie wins field by field, and the
 * tenant's presentation stands in only for the defaults. `tenantPresentation` is the owner's
 * presentation, which only a share or guest request fetches; a caller that has not fetched it
 * passes null and must not serve those viewers.
 */
export function displayPreferenceSources(
  locals: App.Locals,
  cookies: Pick<Cookies, "get">,
  tenantPresentation: UserDisplayPreferences | null
): DisplayPreferenceSources {
  return {
    serverPrefs: locals.isShareHost
      ? tenantPresentation
      : locals.isAuthenticated
        ? (locals.user?.preferences ?? null)
        : null,
    cookiePrefs: locals.isShareHost
      ? null
      : parseDevicePreferences(cookies.get(PREFS_COOKIE_NAME), cookies.get(UNITS_COOKIE_NAME)),
    tenantPreferences: locals.isGuestSession ? tenantPresentation : null,
    tenantDefault: locals.isAuthenticated ? locals.user?.defaultGlucoseUnits : undefined,
  };
}

/**
 * One request's display-preference payloads, highest precedence first, in the browser's own
 * resolution order: the account's saved preferences, this device's cookie, the owner's
 * presentation for a share or guest viewer, then the tenant's default units.
 */
export function displayPreferenceLayers(sources: DisplayPreferenceSources): UserDisplayPreferences[] {
  const { serverPrefs, cookiePrefs, tenantPreferences, tenantDefault } = sources;
  return [
    hasStoredPreferences(serverPrefs) ? serverPrefs : null,
    cookiePrefs,
    hasStoredPreferences(tenantPreferences) ? tenantPreferences : null,
    tenantDefault ? { glucoseUnits: tenantDefault } : null,
  ].filter((prefs) => prefs !== null && prefs !== undefined);
}
