import { ApiClient } from "./api-client";
import { browser } from "$app/environment";
import { env as publicEnv } from "$env/dynamic/public";

/**
 * Client-side API client instance This should be used in the browser when you
 * don't have access to locals
 */
let clientApiClient: ApiClient | null = null;

/**
 * Get the API client for client-side usage This creates a new instance with the
 * browser's native fetch
 */
export function getApiClient(): ApiClient {
  if (!browser) {
    throw new Error(
      "getApiClient() should only be called in the browser. Use event.locals.apiClient in server-side code."
    );
  }

  if (!clientApiClient) {
    const apiBaseUrl = publicEnv.PUBLIC_API_URL || "http://localhost:1612";
    const httpClient = { fetch: window.fetch.bind(window) };
    clientApiClient = new ApiClient(apiBaseUrl, httpClient);
  }

  return clientApiClient;
}

/**
 * Reset the client-side API client instance Useful for testing or when
 * configuration changes
 */
export function resetApiClient(): void {
  clientApiClient = null;
}
