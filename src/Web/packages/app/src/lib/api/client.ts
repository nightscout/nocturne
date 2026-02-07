import { ApiClient } from "./api-client";
import { browser } from "$app/environment";
import { getActingAsHeaders } from "$lib/stores/acting-as";

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
    // Use empty base URL - requests will go to same origin (SvelteKit server)
    // which proxies /api/* requests to the backend via hooks.server.ts
    // This avoids cross-origin issues since cookies are sent with same-origin requests
    const apiBaseUrl = "";
    // Wrap fetch to include credentials and acting-as headers
    const httpClient = {
      fetch: (url: RequestInfo, init?: RequestInit): Promise<Response> => {
        const actingAsHeaders = getActingAsHeaders();
        const headers = new Headers(init?.headers);
        for (const [key, value] of Object.entries(actingAsHeaders)) {
          headers.set(key, value);
        }
        return window.fetch(url, {
          ...init,
          headers,
          credentials: 'include',
        });
      }
    };
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
