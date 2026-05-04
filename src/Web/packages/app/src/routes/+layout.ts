import { browser } from '$app/environment'
// WUCHALE-DISABLED: wuchale temporarily disabled
// import locales from '../../../../supportedLocales.json'
// import { loadLocale } from 'wuchale/load-utils'
import {
    preferredLanguage,
    isSupportedLocale,
    type SupportedLocale,
} from '$lib/stores/appearance-store.svelte'
// so that the loaders are registered, only here, not required in nested ones (below)
// import '../../../../locales/main.loader.svelte.js'
// import '../../../../locales/js.loader.js'

import type { LayoutLoad } from './$types'

export const load: LayoutLoad = async ({ url, data }) => {
    // Query param takes highest priority
    const queryLocale = url.searchParams.get('locale')

    // Determine the locale to use
    let locale: SupportedLocale = 'en'

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
            preferredLanguage.current = userPreference
            locale = userPreference
            // Also update the cookie
            document.cookie = `nocturne-language=${userPreference};path=/;max-age=31536000;SameSite=Lax`
        }
    }

    // WUCHALE-DISABLED: wuchale temporarily disabled — locale dynamic load skipped.
    void locale

    return data
}