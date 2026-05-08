/**
 * Authentication Remote Functions
 *
 * Server-side functions for handling authentication using SvelteKit's remote functions.
 * These use Zod for validation and the API client for backend communication.
 *
 * Password-based auth has been removed in favor of passkey authentication.
 * Passkey WebAuthn ceremony functions are in the generated remote functions
 * (passkeys.generated.remote.ts). The WebAuthn browser API calls
 * (startRegistration/startAuthentication) run client-side in the components.
 */

import { z } from "zod";
import { query, command, getRequestEvent } from "$app/server";

import type { OidcProviderInfo } from "$lib/api/generated/nocturne-api-client";
import { AUTH_COOKIE_NAMES } from "$lib/config/auth-cookies";

// ============================================================================
// Helper Functions
// ============================================================================

/**
 * Get API client from request event, handling misconfiguration gracefully
 */
function getApiClient() {
  const event = getRequestEvent();
  if (!event?.locals?.apiClient) {
    throw new Error(
      "API client not configured. Please check your server configuration."
    );
  }
  return event.locals.apiClient;
}

/**
 * Safely call API and handle connection errors
 */
async function safeApiCall<T>(
  fn: () => Promise<T>,
  fallback?: T
): Promise<T | null> {
  try {
    return await fn();
  } catch (error) {
    // Log error but don't expose details to client
    console.error("API call failed:", error);

    // Check for specific error types
    if (error instanceof Error) {
      // Connection refused or network error
      if (
        error.message.includes("ECONNREFUSED") ||
        error.message.includes("fetch failed")
      ) {
        console.error("Cannot connect to API server");
      }
    }

    if (fallback !== undefined) {
      return fallback;
    }

    return null;
  }
}

// ============================================================================
// Query Functions
// ============================================================================

/**
 * Get OIDC provider configuration
 * Returns enabled OIDC providers for external authentication
 */
export const getOidcProviders = query(async () => {
  const result = await safeApiCall(async () => {
    const api = getApiClient();
    const providers = await api.oidc.getProviders();
    return {
      enabled: providers && providers.length > 0,
      providers: providers ?? [],
    };
  });

  // Return safe defaults if API is unavailable
  return (
    result ?? {
      enabled: false,
      providers: [] as OidcProviderInfo[],
    }
  );
});

/**
 * Get current authentication state
 * Used to check if user is already logged in
 */
export const getAuthState = query(async () => {
  const event = getRequestEvent();
  if (!event) {
    return { isAuthenticated: false, user: null };
  }

  return {
    isAuthenticated: event.locals.isAuthenticated ?? false,
    user: event.locals.user ?? null,
  };
});

/**
 * Get current session info
 * Used by client-side store to check authentication state
 */
export const getSessionInfo = query(async () => {
  const event = getRequestEvent();
  if (!event) {
    return {
      isAuthenticated: false,
      user: null,
    };
  }

  const api = getApiClient();

  try {
    const session = await api.oidc.getSession();
    return {
      isAuthenticated: session?.isAuthenticated ?? false,
      subjectId: session?.subjectId,
      name: session?.name,
      email: session?.email,
      avatarUrl: session?.avatarUrl,
      roles: session?.roles ?? [],
      permissions: session?.permissions ?? [],
      expiresAt: session?.expiresAt,
    };
  } catch (error) {
    console.error("Failed to get session:", error);
    return {
      isAuthenticated: false,
      user: null,
    };
  }
});

/**
 * Get available OIDC providers
 */
export const getProvidersInfo = query(async () => {
  const api = getApiClient();

  try {
    const providers = await api.oidc.getProviders();
    return {
      providers: providers?.map((p) => ({
        id: p.id,
        name: p.name,
        icon: p.icon,
        buttonColor: p.buttonColor,
      })) ?? [],
    };
  } catch (error) {
    console.error("Failed to get providers:", error);
    return { providers: [] };
  }
});

/**
 * Refresh the current session tokens
 */
export const refreshSession = command(async () => {
  const event = getRequestEvent();
  if (!event) {
    return { success: false };
  }

  const api = getApiClient();

  try {
    const result = await api.oidc.refresh();

    // Update cookies if new tokens are returned
    if (result.accessToken) {
      const isSecure = event.url.protocol === "https:";
      const refreshMaxAge = result.refreshExpiresIn || 60 * 60 * 24 * 7;

      event.cookies.set(AUTH_COOKIE_NAMES.accessToken, result.accessToken, {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: result.expiresIn || 3600,
      });

      // Save the rotated refresh token — the API revokes the old one on
      // each use when RotateRefreshTokens is enabled (the default), so we
      // must persist the new token or the next refresh will fail.
      if (result.refreshToken) {
        event.cookies.set(AUTH_COOKIE_NAMES.refreshToken, result.refreshToken, {
          path: "/",
          httpOnly: true,
          secure: isSecure,
          sameSite: "lax",
          maxAge: refreshMaxAge,
        });
      }

      // Match refresh token lifetime so frontend stays authenticated across refreshes
      event.cookies.set("IsAuthenticated", "true", {
        path: "/",
        httpOnly: false,
        secure: isSecure,
        sameSite: "lax",
        maxAge: refreshMaxAge,
      });
    }

    return {
      success: true,
      expiresAt: result.expiresAt,
    };
  } catch (error) {
    console.error("Failed to refresh session:", error);
    return { success: false };
  }
});

/**
 * Logout and clear session cookies
 */
export const logoutSession = command(z.string().optional(), async (_providerId) => {
  const event = getRequestEvent();
  if (!event) {
    return { success: false };
  }

  const api = getApiClient();

  try {
    // Try to revoke on the backend
    await api.oidc.logout();

    // Clear all auth cookies
    event.cookies.delete(AUTH_COOKIE_NAMES.accessToken, { path: "/" });
    event.cookies.delete(AUTH_COOKIE_NAMES.refreshToken, { path: "/" });
    event.cookies.delete("IsAuthenticated", { path: "/" });

    return { success: true };
  } catch (error) {
    console.error("Failed to logout:", error);

    // Still clear cookies even if backend call fails
    event.cookies.delete(AUTH_COOKIE_NAMES.accessToken, { path: "/" });
    event.cookies.delete(AUTH_COOKIE_NAMES.refreshToken, { path: "/" });
    event.cookies.delete("IsAuthenticated", { path: "/" });

    return { success: true };
  }
});

/**
 * Set auth cookies after successful passkey login.
 * Called from the client after the passkey completion endpoint returns tokens.
 */
export const setAuthCookies = command(
  z.object({
    accessToken: z.string(),
    refreshToken: z.string().optional(),
    expiresIn: z.number().optional(),
    refreshExpiresIn: z.number().optional(),
  }),
  async (data) => {
    const event = getRequestEvent();
    if (!event) {
      return { success: false };
    }

    const isSecure = event.url.protocol === "https:";

    event.cookies.set(AUTH_COOKIE_NAMES.accessToken, data.accessToken, {
      path: "/",
      httpOnly: true,
      secure: isSecure,
      sameSite: "lax",
      maxAge: data.expiresIn || 3600,
    });

    const refreshMaxAge = data.refreshExpiresIn || 60 * 60 * 24 * 7;

    if (data.refreshToken) {
      event.cookies.set(AUTH_COOKIE_NAMES.refreshToken, data.refreshToken, {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: refreshMaxAge,
      });
    }

    event.cookies.set("IsAuthenticated", "true", {
      path: "/",
      httpOnly: false,
      secure: isSecure,
      sameSite: "lax",
      maxAge: refreshMaxAge,
    });

    return { success: true };
  }
);
