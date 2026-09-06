import { env } from "$env/dynamic/private";
import { env as publicEnv } from "$env/dynamic/public";
import { ApiClient } from "$lib/api/api-client.generated";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import {
  INSTANCE_KEY_HEADER,
  INSTANCE_SERVICE_HEADER,
  INSTANCE_SERVICE_NAME,
} from "./instance-key";
import {
  propagateAuthCookies,
  type CookieSetter,
} from "./auth-cookie-propagation";

/**
 * Helper to get the API base URL (server-side internal or public).
 */
export function getApiBaseUrl(): string | null {
  return env.NOCTURNE_API_URL || publicEnv.PUBLIC_API_URL || null;
}

export interface ServerHttpClientOptions {
  accessToken?: string;
  refreshToken?: string;
  guestSessionToken?: string;
  platformAccessToken?: string;
  recoverySessionToken?: string;
  hashedInstanceKey?: string | null;
  extraHeaders?: Record<string, string>;
  responseCookies?: CookieSetter;
  /**
   * Sink for Set-Cookie headers SvelteKit's cookie jar cannot hold, appended verbatim to the
   * outgoing response by a handler. See propagateAuthCookies.
   */
  rawSetCookies?: string[];
  signal?: AbortSignal;
}

/** A minimal `{ fetch }` http client as consumed by the generated ApiClient. */
export interface ServerHttpClient {
  fetch: (url: RequestInfo, init?: RequestInit) => Promise<Response>;
}

/**
 * Build a server-side fetch that forwards service auth (instance key), the
 * caller's session cookies, and extra headers to the backend, and propagates
 * any auth-cookie rotation back onto the outgoing SvelteKit response.
 *
 * Exposed separately from {@link createServerApiClient} so callers that need a
 * raw request against the backend (e.g. probing a legacy `/api/v1/*` endpoint
 * the typed client doesn't surface) reuse the exact same auth-forwarding and
 * token-rotation handling instead of reimplementing it.
 */
export function createServerHttpClient(
  fetchFn: typeof fetch,
  options?: ServerHttpClientOptions
): ServerHttpClient {
  return {
    fetch: async (url: RequestInfo, init?: RequestInit): Promise<Response> => {
      const headers = new Headers(init?.headers);

      if (options?.hashedInstanceKey) {
        headers.set(INSTANCE_KEY_HEADER, options.hashedInstanceKey);
        // Declare this as a genuine service call so the API honors the key.
        headers.set(INSTANCE_SERVICE_HEADER, INSTANCE_SERVICE_NAME);
      }

      if (options?.extraHeaders) {
        for (const [key, value] of Object.entries(options.extraHeaders)) {
          headers.set(key, value);
        }
      }

      const cookies: string[] = [];
      if (options?.accessToken) {
        cookies.push(`${AUTH_COOKIE_NAMES.accessToken}=${options.accessToken}`);
      }
      if (options?.refreshToken) {
        cookies.push(`${AUTH_COOKIE_NAMES.refreshToken}=${options.refreshToken}`);
      }
      if (options?.guestSessionToken) {
        cookies.push(`${AUTH_COOKIE_NAMES.guestSession}=${options.guestSessionToken}`);
      }
      if (options?.platformAccessToken) {
        cookies.push(
          `${AUTH_COOKIE_NAMES.platformAccess}=${options.platformAccessToken}`
        );
      }
      if (options?.recoverySessionToken) {
        cookies.push(
          `${AUTH_COOKIE_NAMES.recoverySession}=${options.recoverySessionToken}`
        );
      }
      if (cookies.length > 0) {
        headers.set("Cookie", cookies.join("; "));
      }

      const boundSignal = options?.signal;
      const callSignal = init?.signal;
      const mergedSignal =
        boundSignal && callSignal
          ? AbortSignal.any([boundSignal, callSignal])
          : boundSignal ?? callSignal;

      const response = await fetchFn(url, {
        ...init,
        headers,
        signal: mergedSignal,
      });

      if (options?.responseCookies) {
        const rawSink = options.rawSetCookies;
        propagateAuthCookies(
          response.headers.getSetCookie(),
          options.responseCookies,
          rawSink && ((header) => rawSink.push(header))
        );
      }

      return response;
    },
  };
}

/**
 * Create an API client with custom fetch that includes auth headers.
 *
 * When `responseCookies` is provided, any auth-related Set-Cookie headers
 * on the response are forwarded onto the outgoing SvelteKit response so
 * that token rotation performed by the API middleware reaches the browser.
 * Without this, SSR-initiated calls would silently rotate tokens that
 * never make it back to the client, causing the next request to fail auth
 * (since the old refresh token is now revoked).
 */
export function createServerApiClient(
  baseUrl: string,
  fetchFn: typeof fetch,
  options?: ServerHttpClientOptions
): ApiClient {
  return new ApiClient(baseUrl, createServerHttpClient(fetchFn, options));
}
