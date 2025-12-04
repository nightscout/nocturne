/**
 * Server-side API client utilities for remote functions
 * Uses getRequestEvent to access the authenticated API client from locals
 */
import { getRequestEvent } from '$app/server';
import type { ApiClient } from '$lib/api/api-client';

/**
 * Get the authenticated API client from the request event
 * This should only be called within remote functions (query/command)
 */
export function getApiClient(): ApiClient {
	const event = getRequestEvent();
	return event.locals.apiClient;
}

/**
 * Get the current user from locals
 */
export function getUser() {
	const event = getRequestEvent();
	return event.locals.user;
}

/**
 * Check if the current request is authenticated
 */
export function isAuthenticated(): boolean {
	const event = getRequestEvent();
	return event.locals.isAuthenticated;
}

/**
 * Get URL search params from the current request
 */
export function getUrlParams(): URLSearchParams {
	const event = getRequestEvent();
	return event.url.searchParams;
}

/**
 * Get the current URL
 */
export function getUrl(): URL {
	const event = getRequestEvent();
	return event.url;
}

/**
 * Get route params from the current request
 */
export function getParams(): Record<string, string> {
	const event = getRequestEvent();
	return event.params;
}
