import { createHash } from "crypto";
import { env } from "$env/dynamic/private";
import { env as publicEnv } from "$env/dynamic/public";
import { ApiClient } from "$lib/api/api-client.generated";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
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

/**
 * Helper to get the hashed instance key for service authentication.
 */
export function getHashedInstanceKey(): string | null {
  const instanceKey = env.INSTANCE_KEY;
  return instanceKey
    ? createHash("sha1").update(instanceKey).digest("hex").toLowerCase()
    : null;
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
  options?: {
    accessToken?: string;
    refreshToken?: string;
    guestSessionToken?: string;
    hashedInstanceKey?: string | null;
    extraHeaders?: Record<string, string>;
    responseCookies?: CookieSetter;
  }
): ApiClient {
  const httpClient = {
    fetch: async (url: RequestInfo, init?: RequestInit): Promise<Response> => {
      const headers = new Headers(init?.headers);

      if (options?.hashedInstanceKey) {
        headers.set("X-Instance-Key", options.hashedInstanceKey);
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
        cookies.push(`nocturne-guest-session=${options.guestSessionToken}`);
      }
      if (cookies.length > 0) {
        headers.set("Cookie", cookies.join("; "));
      }

      const response = await fetchFn(url, {
        ...init,
        headers,
      });

      if (options?.responseCookies) {
        propagateAuthCookies(
          response.headers.getSetCookie(),
          options.responseCookies
        );
      }

      return response;
    },
  };

  return new ApiClient(baseUrl, httpClient);
}
