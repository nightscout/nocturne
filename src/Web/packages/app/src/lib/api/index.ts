import type { RequestEvent } from '@sveltejs/kit';
import { getApiClient } from './client';
import { browser } from '$app/environment';

/**
 * Universal API client getter that works on both server and client
 * @param event - The RequestEvent (only needed on server-side)
 * @returns ApiClient instance
 */
export function getUniversalApiClient(event?: RequestEvent) {
	if (browser) {
		// Client-side: use the client utility
		return getApiClient();
	} else {
		// Server-side: use the event locals
		if (!event) {
			throw new Error('RequestEvent is required for server-side API client access');
		}
		return event.locals.apiClient;
	}
}

// Re-export everything from the main API client
export * from './api-client.generated';
export * from './client';
