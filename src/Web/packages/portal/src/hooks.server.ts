import type { Handle, HandleFetch } from '@sveltejs/kit';
import { env } from '$env/dynamic/private';
import { dev } from '$app/environment';
import { runWithLocale, loadLocales } from 'wuchale/load-utils/server';
import { sequence } from '@sveltejs/kit/hooks';
import * as main from '../../../locales/main.loader.server.svelte.js'
import * as js from '../../../locales/js.loader.server.js'
import { locales } from '../../../locales/data.js'

// load at server startup
loadLocales(main.key, main.loadIDs, main.loadCatalog, locales)
loadLocales(js.key, js.loadIDs, js.loadCatalog, locales)

// Turn off SSL validation during development for self-signed certs
if (dev) {
	process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';
}

export const proxy: Handle = async ({ event, resolve }) => {
	// Proxy /api requests to the backend
	if (event.url.pathname.startsWith('/api')) {
		const apiUrl = env.VITE_PORTAL_API_URL;
		if (!apiUrl) {
			console.error('VITE_PORTAL_API_URL is not defined');
			return new Response('Configuration error: VITE_PORTAL_API_URL is missing', { status: 500 });
		}

		const targetUrl = new URL(event.url.pathname + event.url.search, apiUrl);
		console.log(`[PROXY] ${event.url.pathname} -> ${targetUrl.toString()}`);

		try {
			const response = await fetch(targetUrl, {
				method: event.request.method,
				headers: event.request.headers,
				body: event.request.method !== 'GET' && event.request.method !== 'HEAD'
					? await event.request.blob()
					: undefined,
				// Important: dupe logic for signals if needed, but usually simple fetch is enough
			});

			return new Response(response.body, {
				status: response.status,
				statusText: response.statusText,
				headers: response.headers
			});
		} catch (err) {
			console.error('Proxy error:', err);
			return new Response('Proxy error', { status: 502 });
		}
	}

	return resolve(event);
};

export const handleFetch: HandleFetch = async ({ event, request, fetch }) => {
	if (request.url.startsWith('http')) {
		const url = new URL(request.url);
		if (url.pathname.startsWith('/api')) {
			const apiUrl = env.VITE_PORTAL_API_URL;
			if (apiUrl) {
				const targetUrl = new URL(url.pathname + url.search, apiUrl).toString();
				console.log(`[FETCH-PROXY] ${url.pathname} -> ${targetUrl}`);
				// Forward the request to the backend
				return fetch(targetUrl, {
					method: request.method,
					headers: request.headers,
					body: request.body,
					duplex: 'half'
				} as any);
			}
		}
	}
	return fetch(request);
};

export const locale: Handle = async ({ event, resolve }) => {
    const locale = event.url.searchParams.get('locale') ?? 'en'
    return await runWithLocale(locale, () => resolve(event))
}


export const handle: Handle = sequence(proxy, locale);