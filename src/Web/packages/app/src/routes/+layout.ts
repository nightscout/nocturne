import locales from '../../../../supportedLocales.json'
import { browser } from '$app/environment'
import { loadLocale } from 'wuchale/load-utils'
import {
    preferredLanguage,
    isSupportedLocale,
    registerPreferenceCookieDomain,
    registerPreferencesWriteThrough,
    reconcilePreferences,
    showTenantDefaultUnits,
    presentOwnerPreferences,
    applyTenantPresentation,
    setLanguage,
    type SupportedLocale,
} from '$lib/stores/appearance-store.svelte'
import { updateDisplayPreferences } from '$lib/api/user-preferences.remote'
// so that the loaders are registered, only here, not required in nested ones (below)
import '../../../../locales/main.loader.svelte.js'
import '../../../../locales/js.loader.js'

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
        // The cookie domain is not registered on a share host. The preference cookie spans the
        // base domain, and the appearance rendered there is the link owner's rather than the
        // viewer's, so a widened write would push it onto the viewer's own tenant.
        if (!data?.isShareHost) {
            registerPreferenceCookieDomain(data?.baseDomain)
        }
        // Only a viewer with an account has a subject for the backend to save to; a layout
        // reload that loses the account withdraws the callback rather than leaving it behind.
        registerPreferencesWriteThrough(
            data?.hasAccount ? (prefs) => updateDisplayPreferences(prefs) : null
        )
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

    if (browser && data?.isShareHost) {
        presentOwnerPreferences(data?.serverPreferences)
    } else if (browser && data?.hasAccount) {
        // Server preferences win across devices; an empty server blob seeds from local once.
        reconcilePreferences(data?.serverPreferences)
        showTenantDefaultUnits(data?.user?.defaultGlucoseUnits)
    }

    applyTenantPresentation(data?.tenantPreferences)

    if (browser && locales.includes(locale)) {
        await loadLocale(locale)
    }

    return data
}
