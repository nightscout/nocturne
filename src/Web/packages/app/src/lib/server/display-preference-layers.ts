import type { UserDisplayPreferences } from "#lib/api/index.js";
import { hasStoredPreferences } from "#lib/stores/appearance-store.svelte.js";

/**
 * One request's display-preference payloads, highest precedence first, in the browser's own
 * resolution order: the account's saved preferences, this device's cookie, the owner's
 * presentation for a share or guest viewer, then the tenant's default units.
 */
export function displayPreferenceLayers(sources: {
  serverPrefs: UserDisplayPreferences | null | undefined;
  cookiePrefs: UserDisplayPreferences | null;
  tenantPreferences: UserDisplayPreferences | null;
  tenantDefault: string | null | undefined;
}): UserDisplayPreferences[] {
  const { serverPrefs, cookiePrefs, tenantPreferences, tenantDefault } = sources;
  return [
    hasStoredPreferences(serverPrefs) ? serverPrefs : null,
    cookiePrefs,
    hasStoredPreferences(tenantPreferences) ? tenantPreferences : null,
    tenantDefault ? { glucoseUnits: tenantDefault } : null,
  ].filter((prefs) => prefs !== null && prefs !== undefined);
}
