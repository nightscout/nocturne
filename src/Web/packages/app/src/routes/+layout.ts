import locales from '../../../../supportedLocales.json'
import { browser } from '$app/environment'
import { loadLocale } from 'wuchale/load-utils'
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
        // Neither is registered on a share host. The preference cookie spans the base domain,
        // and the appearance rendered there is the link owner's rather than the viewer's, so a
        // widened write would push it onto the viewer's own tenant; and the viewer is anonymous,
        // so there is no account behind the link to write through to.
        if (!data?.isShareHost) {
            registerPreferenceCookieDomain(data?.baseDomain)
            registerPreferencesWriteThrough((prefs) => updateDisplayPreferences(prefs))
        }
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

    if (browser && (data?.isAuthenticated || data?.isShareHost)) {
        // Server preferences win across devices; an empty server blob seeds from local once.
        // On a share host they are the link owner's, and hydrating them here is what keeps the
        // client from replacing the server-rendered view with this browser's defaults.
        reconcilePreferences(data?.serverPreferences)
    }

    if (browser && locales.includes(locale)) {
        await loadLocale(locale)
    }

    return data
}
