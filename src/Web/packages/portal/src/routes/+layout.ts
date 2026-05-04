import locales from '../../../../supportedLocales.json'
import { browser } from '$app/environment'
import { loadLocale } from 'wuchale/load-utils'
import {
    preferredLanguage,
    isSupportedLocale,
    type SupportedLocale,
} from '@nocturne/app/stores/appearance-store.svelte'
// so that the loaders are registered, only here, not required in nested ones (below)
import '../../../../locales/main.loader.svelte.js'
import '../../../../locales/js.loader.js'

import { building } from '$app/environment'
import type { Load } from '@sveltejs/kit'

export const load: Load = async ({ url }) => {
    // Query param takes highest priority (not available during prerendering)
    const queryLocale = building ? null : url.searchParams.get('locale')

    // Determine the locale to use
    let locale: SupportedLocale = 'en'

    if (queryLocale && isSupportedLocale(queryLocale)) {
        // 1. Query param override
        locale = queryLocale
    } else if (browser) {
        // On client: use persisted state from localStorage
        locale = preferredLanguage.current
    }

    if (browser && locales.includes(locale)) {
        await loadLocale(locale)
    }
}