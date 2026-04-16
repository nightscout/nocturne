import type { Handle } from "@sveltejs/kit";
import { randomUUID } from "$lib/utils";
import type { HandleServerError } from "@sveltejs/kit";
import { env } from "$env/dynamic/private";
import { env as publicEnv } from "$env/dynamic/public";
import { dev } from "$app/environment";
import {
  getApiBaseUrl,
  getHashedInstanceKey,
  createServerApiClient,
} from "$lib/server/api-client-factory";
import { sequence } from "@sveltejs/kit/hooks";
import type { AuthUser } from "./app.d";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";
import { runWithLocale, loadLocales } from 'wuchale/load-utils/server';
import * as main from '../../../locales/main.loader.server.svelte.js'
import * as js from '../../../locales/js.loader.server.js'
import { locales } from '../../../locales/data.js'
import supportedLocales from '../../../supportedLocales.json';
import { LANGUAGE_COOKIE_NAME } from "$lib/stores/appearance-store.svelte";
import { isPublicRoute, STATIC_ASSET_PREFIXES } from "$lib/config/public-routes";

// load at server startup
loadLocales(main.key, main.loadIDs, main.loadCatalog, locales)
loadLocales(js.key, js.loadIDs, js.loadCatalog, locales)

// Turn off SSL validation during development for self-signed certs
if (dev) {
  process.env.NODE_TLS_REJECT_UNAUTHORIZED = "0";
}

/**
 * Auth handler - extracts session from cookies and validates with API
 */
const authHandle: Handle = async ({ event, resolve }) => {
  // Initialize auth state as unauthenticated
  event.locals.user = null;
  event.locals.isAuthenticated = false;
  event.locals.isPlatformAdmin = false;

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
    // Create a temporary API client with auth tokens for session validation.
    // `responseCookies` lets the client forward any auth-cookie rotations
    // performed by the API (via SessionCookieHandler auto-refresh) back to
    // the browser, so rotated refresh tokens don't silently disappear.
    const refreshToken = event.cookies.get(AUTH_COOKIE_NAMES.refreshToken);
    const hostHeader = event.request.headers.get("host");
    const apiClient = createServerApiClient(apiBaseUrl, fetch, {
      accessToken,
      refreshToken,
      hashedInstanceKey: getHashedInstanceKey(),
      extraHeaders: hostHeader ? { "X-Forwarded-Host": hostHeader } : undefined,
      responseCookies: event.cookies,
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
        preferredLanguage: session.preferredLanguage ?? undefined,
      };

      event.locals.user = user;
      event.locals.isAuthenticated = true;
      event.locals.isPlatformAdmin = session.isPlatformAdmin ?? false;

      // Fetch effective permissions (granted scopes) for the current tenant
      try {
        event.locals.effectivePermissions = await apiClient.myPermissions.getMyPermissions();
      } catch {
        // Non-fatal — permissions will default to empty
      }
    }
  } catch (error) {
    // Log but don't fail the request - user will be treated as unauthenticated
    console.error("Failed to validate session:", error);
  }

  return resolve(event);
};

/**
 * Site security handler - enforces authentication when required, detects setup/recovery mode.
 * Uses shared public route list to determine which paths bypass all gates.
 */
const siteSecurityHandle: Handle = async ({ event, resolve }) => {
  const apiBaseUrl = getApiBaseUrl();

  if (!apiBaseUrl) {
    return resolve(event);
  }

  const pathname = event.url.pathname;

  // Skip the status probe entirely for static assets, for pages that ARE
  // the setup/recovery/auth destinations (probing those would cause infinite
  // redirect loops), and for external webhook/bot endpoints that must respond
  // regardless of setup state — third-party services like Discord cannot
  // follow HTML redirects and will treat any non-2xx as a hard failure.
  const skipProbe =
    STATIC_ASSET_PREFIXES.some((p) => pathname.startsWith(p)) ||
    pathname.startsWith("/setup") ||
    pathname.startsWith("/auth") ||
    pathname.startsWith("/api/v4/webhooks") ||
    pathname.startsWith("/api/v4/bot");

  if (skipProbe) {
    return resolve(event);
  }

  // Probe the API for setup/recovery mode and site-level requireAuthentication.
  try {
    if (!event.locals.siteSecurityChecked) {
      const hostHeader = event.request.headers.get("host");
      const apiClient = createServerApiClient(apiBaseUrl, fetch, {
        hashedInstanceKey: getHashedInstanceKey(),
        extraHeaders: hostHeader ? { "X-Forwarded-Host": hostHeader } : undefined,
      });

      const status = await apiClient.status.getStatus();
      const requireAuth = status?.settings?.["requireAuthentication"] === true;

      event.locals.requireAuthentication = requireAuth;
      event.locals.siteSecurityChecked = true;
    }

    // Only enforce requireAuthentication on non-public routes
    if (!isPublicRoute(pathname) && event.locals.requireAuthentication && !event.locals.isAuthenticated) {
      const returnUrl = encodeURIComponent(pathname + event.url.search);
      return new Response(null, {
        status: 303,
        headers: {
          Location: `/auth/login?returnUrl=${returnUrl}`,
        },
      });
    }
  } catch (error) {
    if (error && typeof error === "object" && "status" in error) {
      const status = (error as any).status;

      // Tenant not found — redirect to marketing site if configured
      if (status === 404) {
        const marketingUrl = env.MARKETING_URL;
        if (marketingUrl) {
          return new Response(null, {
            status: 302,
            headers: { Location: marketingUrl },
          });
        }
      }

      if (status === 503) {
        try {
          const body = JSON.parse((error as any).response ?? "{}");
          if (body.setupRequired) {
            return new Response(null, {
              status: 303,
              headers: { Location: "/setup" },
            });
          }
          if (body.recoveryMode) {
            return new Response(null, {
              status: 303,
              headers: { Location: "/auth/recovery" },
            });
          }
        } catch {
          // Couldn't parse, fall through
        }
      }
    }
    console.error("Failed to check site security settings:", error);
  }

  return resolve(event);
};

// Proxy handler for /api requests
const proxyHandle: Handle = async ({ event, resolve }) => {
  // Check if the request is for /api (but not SvelteKit-handled routes like webhooks and bot dispatch)
  const path = event.url.pathname;
  if (path.startsWith("/api") && !path.startsWith("/api/v4/webhooks") && !path.startsWith("/api/v4/bot")) {
    const apiBaseUrl = getApiBaseUrl();
    if (!apiBaseUrl) {
      throw new Error(
        "Neither NOCTURNE_API_URL nor PUBLIC_API_URL is defined. Please set one in your environment variables."
      );
    }

    const hashedInstanceKey = getHashedInstanceKey();

    // Construct the target URL
    const targetUrl = new URL(event.url.pathname + event.url.search, apiBaseUrl);

    // Forward the request to the backend API
    const headers = new Headers(event.request.headers);
    // Forward original Host for tenant resolution behind reverse proxies
    const originalHost = event.request.headers.get("host");
    if (originalHost) {
      headers.set("X-Forwarded-Host", originalHost);
    }
    if (hashedInstanceKey) {
      headers.set("X-Instance-Key", hashedInstanceKey);
    }

    // Forward both access and refresh tokens for authentication and token refresh
    const accessToken = event.cookies.get(AUTH_COOKIE_NAMES.accessToken);
    const refreshToken = event.cookies.get(AUTH_COOKIE_NAMES.refreshToken);
    const cookies: string[] = [];
    if (accessToken) {
      cookies.push(`${AUTH_COOKIE_NAMES.accessToken}=${accessToken}`);
    }
    if (refreshToken) {
      cookies.push(`${AUTH_COOKIE_NAMES.refreshToken}=${refreshToken}`);
    }
    if (cookies.length > 0) {
      headers.set("Cookie", cookies.join("; "));
    }

    const proxyResponse = await fetch(targetUrl.toString(), {
      method: event.request.method,
      headers,
      body: event.request.method !== "GET" && event.request.method !== "HEAD"
        ? await event.request.arrayBuffer()
        : undefined,
    });


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

  // Forward X-Acting-As header if present (follower context)
  const extraHeaders: Record<string, string> = {};
  const actingAs = event.request.headers.get("x-acting-as");
  if (actingAs) {
    extraHeaders["X-Acting-As"] = actingAs;
  }

  // Forward the original Host for tenant resolution behind reverse proxies
  const originalHost = event.request.headers.get("host");
  if (originalHost) {
    extraHeaders["X-Forwarded-Host"] = originalHost;
  }

  // Create API client with SvelteKit's fetch, auth headers, and both tokens.
  // `responseCookies` lets any token rotation performed by the backend's
  // session middleware (during remote function / load function calls) flow
  // back to the browser as Set-Cookie on the outgoing SvelteKit response.
  event.locals.apiClient = createServerApiClient(apiBaseUrl, event.fetch, {
    accessToken,
    refreshToken,
    hashedInstanceKey: getHashedInstanceKey(),
    extraHeaders,
    responseCookies: event.cookies,
  });

  return resolve(event);
};

export const handleError: HandleServerError = async ({ error, event }) => {
  const errorId = randomUUID();
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

/**
 * Parse Accept-Language header and find the best matching supported locale
 */
function parseAcceptLanguage(header: string | null, supported: Set<string>): string | null {
  if (!header) return null;

  // Parse Accept-Language header (e.g., "en-US,en;q=0.9,fr;q=0.8")
  const languages = header.split(",").map((lang) => {
    const [code, qValue] = lang.trim().split(";q=");
    return {
      code: code.split("-")[0].toLowerCase(), // Use primary language tag
      quality: qValue ? parseFloat(qValue) : 1.0,
    };
  });

  // Sort by quality descending
  languages.sort((a, b) => b.quality - a.quality);

  // Find the first supported language
  for (const { code } of languages) {
    if (supported.has(code)) {
      return code;
    }
  }

  return null;
}

/**
 * Resolve locale using priority cascade:
 * 1. Query param override (?locale=fr)
 * 2. Cookie (nocturne-language) - synced from client localStorage
 * 3. User's backend preference (if authenticated)
 * 4. Environment default (PUBLIC_DEFAULT_LANGUAGE)
 * 5. Browser Accept-Language header
 * 6. Ultimate fallback: 'en'
 */
function resolveLocale(event: Parameters<Handle>[0]["event"]): string {
  const supported = new Set(supportedLocales);

  // 1. Query param override
  const queryLocale = event.url.searchParams.get("locale");
  if (queryLocale && supported.has(queryLocale)) {
    return queryLocale;
  }

  // 2. Cookie (set by client from localStorage)
  const cookieLocale = event.cookies.get(LANGUAGE_COOKIE_NAME);
  if (cookieLocale && supported.has(cookieLocale)) {
    return cookieLocale;
  }

  // 3. User's backend preference (if authenticated)
  const userPreference = event.locals.user?.preferredLanguage;
  if (userPreference && supported.has(userPreference)) {
    return userPreference;
  }

  // 4. Environment default
  const envDefault = publicEnv.PUBLIC_DEFAULT_LANGUAGE;
  if (envDefault && supported.has(envDefault)) {
    return envDefault;
  }

  // 5. Browser Accept-Language header
  const acceptLang = event.request.headers.get("accept-language");
  const browserLocale = parseAcceptLanguage(acceptLang, supported);
  if (browserLocale) {
    return browserLocale;
  }

  // 6. Ultimate fallback
  return "en";
}

export const locale: Handle = async ({ event, resolve }) => {
  const locale = resolveLocale(event);
  return await runWithLocale(locale, () => resolve(event));
}

// Chain the auth handler, site security handler, proxy handler, and API client handler
export const handle: Handle = sequence(authHandle, siteSecurityHandle, proxyHandle, apiClientHandle, locale);
