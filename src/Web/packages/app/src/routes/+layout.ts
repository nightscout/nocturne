import { browser } from '$app/environment'
// WUCHALE-DISABLED: wuchale temporarily disabled
// import locales from '../../../../supportedLocales.json'
// import { loadLocale } from 'wuchale/load-utils'
import {
    preferredLanguage,
    isSupportedLocale,
    registerPreferenceCookieDomain,
    registerPreferencesWriteThrough,
    reconcilePreferences,
    setLanguage,
    type SupportedLocale,
} from '$lib/stores/appearance-store.svelte'
import { updateDisplayPreferences } from '$lib/api/user-preferences.remote'
// so that the loaders are registered, only here, not required in nested ones (below)
// import '../../../../locales/main.loader.svelte.js'
// import '../../../../locales/js.loader.js'

import type { LayoutLoad } from './$types'

export const load: LayoutLoad = async ({ url, data }) => {
    // Query param takes highest priority
    const queryLocale = url.searchParams.get('locale')

    // Determine the locale to use
    let locale: SupportedLocale = 'en'

    // Wire up per-user display-preference sync (units, time format, theme, chart style).
    // Registering the backend write-through here keeps the store free of server-remote imports,
    // and the cookie domain must be registered before anything below writes a cookie.
    if (browser) {
        registerPreferenceCookieDomain(data?.baseDomain)
        registerPreferencesWriteThrough((prefs) => updateDisplayPreferences(prefs))
    }

    if (queryLocale && isSupportedLocale(queryLocale)) {
        // 1. Query param override
        locale = queryLocale
    } else if (browser) {
        // On client: use persisted state
        locale = preferredLanguage.current

        // If user is logged in and their backend preference differs from localStorage,
        // sync localStorage to match (handles new device case)
        const userPreference = data?.user?.preferredLanguage
        if (userPreference && isSupportedLocale(userPreference) && userPreference !== preferredLanguage.current) {
            await setLanguage(userPreference)
            locale = userPreference
        }
    }

    if (browser && data?.isAuthenticated) {
        // Server preferences win across devices; an empty server blob seeds from local once.
        reconcilePreferences(data?.user?.preferences)
    }

    // WUCHALE-DISABLED: wuchale temporarily disabled — locale dynamic load skipped.
    void locale

    return data
}