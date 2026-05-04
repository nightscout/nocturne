import type { Handle } from '@sveltejs/kit';
import { building } from '$app/environment';
import { env as publicEnv } from '$env/dynamic/public';
import { runWithLocale, loadLocales } from 'wuchale/load-utils/server';
import * as main from '../../../locales/main.loader.server.svelte.js'
import * as js from '../../../locales/js.loader.server.js'
import { locales } from '../../../locales/data.js'
import supportedLocales from '../../../supportedLocales.json';

// load at server startup
loadLocales(main.key, main.loadIDs, main.loadCatalog, locales)
loadLocales(js.key, js.loadIDs, js.loadCatalog, locales)

/** Cookie name for language preference - must match app store */
const LANGUAGE_COOKIE_NAME = 'nocturne-language';

/**
 * Parse Accept-Language header and find the best matching supported locale
 */
function parseAcceptLanguage(header: string | null, supported: Set<string>): string | null {
	if (!header) return null;

	const languages = header.split(',').map((lang) => {
		const [code, qValue] = lang.trim().split(';q=');
		return {
			code: code.split('-')[0].toLowerCase(),
			quality: qValue ? parseFloat(qValue) : 1.0,
		};
	});

	languages.sort((a, b) => b.quality - a.quality);

	for (const { code } of languages) {
		if (supported.has(code)) {
			return code;
		}
	}

	return null;
}

/**
 * Resolve locale using priority cascade (portal has no user auth):
 * 1. Query param override (?locale=fr)
 * 2. Cookie (nocturne-language) - synced from client localStorage
 * 3. Environment default (PUBLIC_DEFAULT_LANGUAGE)
 * 4. Browser Accept-Language header
 * 5. Ultimate fallback: 'en'
 */
function resolveLocale(event: Parameters<Handle>[0]['event']): string {
	const supported = new Set(supportedLocales);

	// 1. Query param override
	const queryLocale = event.url.searchParams.get('locale');
	if (queryLocale && supported.has(queryLocale)) {
		return queryLocale;
	}

	// 2. Cookie (set by client from localStorage)
	const cookieLocale = event.cookies.get(LANGUAGE_COOKIE_NAME);
	if (cookieLocale && supported.has(cookieLocale)) {
		return cookieLocale;
	}

	// 3. Environment default
	const envDefault = publicEnv.PUBLIC_DEFAULT_LANGUAGE;
	if (envDefault && supported.has(envDefault)) {
		return envDefault;
	}

	// 4. Browser Accept-Language header
	const acceptLang = event.request.headers.get('accept-language');
	const browserLocale = parseAcceptLanguage(acceptLang, supported);
	if (browserLocale) {
		return browserLocale;
	}

	// 5. Ultimate fallback
	return 'en';
}

export const handle: Handle = async ({ event, resolve }) => {
	if (building) {
		return resolve(event);
	}
	const locale = resolveLocale(event);
	return await runWithLocale(locale, () => resolve(event));
};
