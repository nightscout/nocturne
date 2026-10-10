import { ApiClient } from "./api-client.generated";
import { browser } from "$app/environment";
import { createAuthenticatedFetch } from "./auth-interceptor";
import { initialReadsStash, takeInitialRead } from "$lib/stores/initial-reads";

/**
 * Client-side API client instance This should be used in the browser when you
 * don't have access to locals
 */
let clientApiClient: ApiClient | null = null;

/**
 * Get the API client for client-side usage This creates a new instance with the
 * browser's native fetch and auth interceptor
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

    // Create the base fetch function with credentials
    const baseFetch = (url: RequestInfo, init?: RequestInit): Promise<Response> => {
      const prefetched = takeInitialRead(initialReadsStash(), url, init?.method);
      if (prefetched) return prefetched;
      return window.fetch(url, {
        ...init,
        credentials: 'include',
      });
    };

    // Wrap fetch with auth interceptor to handle 401 responses
    const authenticatedFetch = createAuthenticatedFetch(baseFetch);

    const httpClient = {
      fetch: authenticatedFetch,
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
