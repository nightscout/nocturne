import type { Handle } from "@sveltejs/kit";
import { ApiClient } from "$lib/api/api-client";
import type { HandleServerError } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import { env as publicEnv } from "$env/dynamic/public";
import { dev } from "$app/environment";
import { createHash } from "crypto";
import { sequence } from "@sveltejs/kit/hooks";
import type { AuthUser } from "./app.d";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { runWithLocale, loadLocales } from 'wuchale/load-utils/server';
import * as main from '../../../locales/main.loader.server.svelte.js'
import * as js from '../../../locales/js.loader.server.js'
import { locales } from '../../../locales/data.js'

// load at server startup
loadLocales(main.key, main.loadIDs, main.loadCatalog, locales)
loadLocales(js.key, js.loadIDs, js.loadCatalog, locales)

// Turn off SSL validation during development for self-signed certs
if (dev) {
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
}

/**
 * Helper to get the API base URL (server-side internal or public)
 */
function getApiBaseUrl(): string | null {
  return env.NOCTURNE_API_URL || publicEnv.PUBLIC_API_URL || null;
}

/**
 * Helper to get the hashed API secret for authentication
 */
function getHashedApiSecret(): string | null {
  const apiSecret = env.API_SECRET;
  return apiSecret
    ? createHash("sha1").update(apiSecret).digest("hex").toLowerCase()
    : null;
}

/**
 * Create an API client with custom fetch that includes auth headers
 */
function createServerApiClient(
  baseUrl: string,
  fetchFn: typeof fetch,
  options?: { accessToken?: string; refreshToken?: string; hashedSecret?: string | null }
): ApiClient {
  const httpClient = {
    fetch: async (url: RequestInfo, init?: RequestInit): Promise<Response> => {
      const headers = new Headers(init?.headers);

      // Add the hashed API secret as authentication
      if (options?.hashedSecret) {
        headers.set("api-secret", options.hashedSecret);
      }

      // Forward auth cookies if provided (both access and refresh for token refresh flow)
      const cookies: string[] = [];
      if (options?.accessToken) {
        cookies.push(`${AUTH_COOKIE_NAMES.accessToken}=${options.accessToken}`);
      }
      if (options?.refreshToken) {
        cookies.push(`${AUTH_COOKIE_NAMES.refreshToken}=${options.refreshToken}`);
      }
      if (cookies.length > 0) {
        headers.set("Cookie", cookies.join("; "));
      }

      return fetchFn(url, {
        ...init,
        headers,
      });
    },
  };

  return new ApiClient(baseUrl, httpClient);
}

/**
 * Auth handler - extracts session from cookies and validates with API
 */
const authHandle: Handle = async ({ event, resolve }) => {
  // Initialize auth state as unauthenticated
  event.locals.user = null;
  event.locals.isAuthenticated = false;

  const apiBaseUrl = getApiBaseUrl();

  if (!apiBaseUrl) {
    return resolve(event);
  }

  // Check for auth cookie
  const authCookie = event.cookies.get("IsAuthenticated");
  const accessToken = event.cookies.get(AUTH_COOKIE_NAMES.accessToken);

  if (!authCookie && !accessToken) {
    // No auth cookies, user is not authenticated
    return resolve(event);
  }

  try {
    // Create a temporary API client with auth tokens for session validation
    const refreshToken = event.cookies.get(AUTH_COOKIE_NAMES.refreshToken);
    const apiClient = createServerApiClient(apiBaseUrl, fetch, {
      accessToken,
      refreshToken,
      hashedSecret: getHashedApiSecret(),
    });

    // Validate session with the API using the typed client
    const session = await apiClient.oidc.getSession();

    if (session?.isAuthenticated && session.subjectId) {
      const user: AuthUser = {
        subjectId: session.subjectId,
        name: session.name ?? "User",
        email: session.email,
        roles: session.roles ?? [],
        permissions: session.permissions ?? [],
        expiresAt: session.expiresAt,
      };

      event.locals.user = user;
      event.locals.isAuthenticated = true;
    }
  } catch (error) {
    // Log but don't fail the request - user will be treated as unauthenticated
    console.error("Failed to validate session:", error);
  }

  return resolve(event);
};

// Proxy handler for /api requests
const proxyHandle: Handle = async ({ event, resolve }) => {
  // Check if the request is for /api
  if (event.url.pathname.startsWith("/api")) {
    const apiBaseUrl = getApiBaseUrl();
    if (!apiBaseUrl) {
      throw new Error(
        "Neither NOCTURNE_API_URL nor PUBLIC_API_URL is defined. Please set one in your environment variables."
      );
    }

    const hashedSecret = getHashedApiSecret();

    // Construct the target URL
    const targetUrl = new URL(event.url.pathname + event.url.search, apiBaseUrl);

    // Forward the request to the backend API
    const headers = new Headers(event.request.headers);
    if (hashedSecret) {
      headers.set("api-secret", hashedSecret);
    }

    // Debug: Log all cookies SvelteKit sees
    const allCookies = event.request.headers.get("cookie");
    console.log(`[PROXY] Request to ${event.url.pathname}`);
    console.log(`[PROXY] All cookies from request: ${allCookies || "(none)"}`);

    // Forward both access and refresh tokens for authentication and token refresh
    const accessToken = event.cookies.get(AUTH_COOKIE_NAMES.accessToken);
    const refreshToken = event.cookies.get(AUTH_COOKIE_NAMES.refreshToken);
    console.log(`[PROXY] Access token: ${accessToken ? accessToken.substring(0, 20) + "..." : "(not found)"}`);
    console.log(`[PROXY] Refresh token: ${refreshToken ? refreshToken.substring(0, 20) + "..." : "(not found)"}`);

    const cookies: string[] = [];
    if (accessToken) {
      cookies.push(`${AUTH_COOKIE_NAMES.accessToken}=${accessToken}`);
    }
    if (refreshToken) {
      cookies.push(`${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}`);
    }
    if (cookies.length > 0) {
      headers.set("Cookie", cookies.join("; "));
      console.log(`[PROXY] Forwarding ${cookies.length} auth cookie(s) to backend`);
    } else {
      console.log(`[PROXY] No auth cookies to forward`);
    }

    const proxyResponse = await fetch(targetUrl.toString(), {
      method: event.request.method,
      headers,
      body: event.request.method !== "GET" && event.request.method !== "HEAD"
        ? await event.request.arrayBuffer()
        : undefined,
    });

    console.log(`[PROXY] Response status: ${proxyResponse.status}`);

    // Return the proxied response
    return new Response(proxyResponse.body, {
      status: proxyResponse.status,
      statusText: proxyResponse.statusText,
      headers: proxyResponse.headers,
    });
  }

  return resolve(event);
};

const apiClientHandle: Handle = async ({ event, resolve }) => {
  const apiBaseUrl = getApiBaseUrl();
  if (!apiBaseUrl) {
    throw new Error(
      "Neither NOCTURNE_API_URL nor PUBLIC_API_URL is defined. Please set one in your environment variables."
    );
  }

  // Get auth tokens from cookies to forward to the backend
  const accessToken = event.cookies.get(AUTH_COOKIE_NAMES.accessToken);
  const refreshToken = event.cookies.get(AUTH_COOKIE_NAMES.refreshToken);


  // Create API client with SvelteKit's fetch, auth headers, and both tokens
  event.locals.apiClient = createServerApiClient(apiBaseUrl, event.fetch, {
    accessToken,
    refreshToken,
    hashedSecret: getHashedApiSecret(),
  });

  return resolve(event);
};

export const handleError: HandleServerError = async ({ error, event }) => {
  const errorId = crypto.randomUUID();
  console.error(`Error ID: ${errorId}`, error);
  console.log(
    `Error occurred during request: ${event.request.method} ${event.request.url}`
  );

  // Extract meaningful error message
  let message = "An unexpected error occurred";
  let details: string | undefined;

  if (error instanceof Error) {
    message = error.message;

    // Check for ApiException-style errors with response property
    const apiError = error as Error & { response?: string; status?: number };
    if (apiError.response) {
      try {
        const parsed = JSON.parse(apiError.response);
        details = parsed.error || parsed.message || apiError.response;
      } catch {
        details = apiError.response;
      }
    }
  } else if (typeof error === "string") {
    message = error;
  }

  return {
    message,
    details,
    errorId,
  };
};

export const locale: Handle = async ({ event, resolve }) => {
    const locale = event.url.searchParams.get('locale') ?? 'en'
    return await runWithLocale(locale, () => resolve(event))
}

// Chain the auth handler, proxy handler, and API client handler
export const handle: Handle = sequence(authHandle, proxyHandle, apiClientHandle, locale);
