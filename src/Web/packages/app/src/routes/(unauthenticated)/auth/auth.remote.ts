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
import { query, command, form, getRequestEvent } from "$app/server";
import { invalid, redirect } from "@sveltejs/kit";

import type { OidcProviderInfo } from "$lib/api/generated/nocturne-api-client";
import { clearAuthCookies } from "$lib/config/auth-cookies";
import { errorStatus } from "$lib/forms/submit-error";
import { safeReturnUrl } from "$lib/server/return-url";

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

    // Cookie propagation is handled automatically by propagateAuthCookies
    // (configured via responseCookies in the API client factory). The API's
    // Set-Cookie headers include the correct Domain attribute (e.g.
    // ".nocturne.run") so cookies update in-place rather than creating
    // duplicate domain-scoped cookies that lead to stale/revoked tokens
    // being sent on subsequent requests.

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

    clearAuthCookies(event.cookies);

    return { success: true };
  } catch (error) {
    console.error("Failed to logout:", error);

    // Still clear cookies even if backend call fails
    clearAuthCookies(event.cookies);

    return { success: true };
  }
});

// ============================================================================
// Form Functions
// ============================================================================

/**
 * Recovery-code sign-in fields. `returnUrl` is a hidden field so a submission
 * without JavaScript lands where the user started; it's reduced to a same-origin
 * path before being used.
 */
const recoveryCodeSchema = z.object({
  username: z.string().trim().min(1, "Enter your username"),
  code: z.string().trim().min(1, "Enter your code"),
  returnUrl: z.string().optional(),
});

/**
 * Authenticator-code fields. The account is named by the step-up token the
 * passkey step returned, not by the person signing in, because the code alone
 * is not a sign-in method.
 */
const authenticatorSchema = z.object({
  stepUpToken: z.string().min(1),
  code: z
    .string()
    .trim()
    .regex(/^\d{6}$/, "Enter the 6-digit code from your authenticator app"),
  returnUrl: z.string().optional(),
});

/**
 * Sign in with a recovery code.
 *
 * Runs entirely on the server, so it works with JavaScript disabled: the
 * browser posts the form, the handler redirects on success, and a rejected code
 * comes back as a field-level issue on the re-rendered page.
 */
export const signInWithRecoveryCode = form(
  recoveryCodeSchema,
  async (data, issue) => {
    const api = getApiClient();

    let verified = false;
    try {
      const result = await api.passkey.recoveryVerify({
        username: data.username,
        code: data.code,
      });
      verified = result?.success === true;
    } catch (err) {
      // Log the status only: the response carries the submitted credentials.
      console.error(
        "Recovery code sign-in failed with status:",
        errorStatus(err) ?? "none"
      );
    }

    // The API deliberately doesn't say which of the two was wrong.
    if (!verified) {
      invalid(issue.code("That username and recovery code don't match."));
    }

    redirect(303, safeReturnUrl(data.returnUrl));
  }
);

/**
 * Finish signing in with a code from an authenticator app, after the passkey
 * step returned a step-up token. Server-side for the same reason as
 * {@link signInWithRecoveryCode}.
 */
export const signInWithAuthenticator = form(
  authenticatorSchema,
  async (data, issue) => {
    const api = getApiClient();

    let verified = false;
    try {
      const result = await api.totp.login({
        stepUpToken: data.stepUpToken,
        code: data.code,
      });
      verified = result?.success === true;
    } catch (err) {
      console.error(
        "Authenticator sign-in failed with status:",
        errorStatus(err) ?? "none"
      );
    }

    if (!verified) {
      invalid(
        issue.code(
          "That code wasn't accepted. Each code works once and expires after 30 seconds — try the current one."
        )
      );
    }

    redirect(303, safeReturnUrl(data.returnUrl));
  }
);

/**
 * Set auth cookies after successful passkey login.
 * Called from the client after the passkey completion endpoint returns tokens.
 *
 * The passkey completion API response already sets auth cookies on the browser
 * via Set-Cookie headers (with the correct Domain attribute). This command
 * validates the session server-side so that propagateAuthCookies can forward
 * any rotated tokens with the proper domain, avoiding duplicate cookies that
 * would cause stale/revoked tokens to linger on the parent domain.
 */
export const setAuthCookies = command(
  z.object({
    accessToken: z.string(),
    refreshToken: z.string().optional(),
    expiresIn: z.number().optional(),
    refreshExpiresIn: z.number().optional(),
  }),
  async () => {
    const event = getRequestEvent();
    if (!event) {
      return { success: false };
    }

    // The browser already has auth cookies from the passkey API's Set-Cookie
    // headers (forwarded through YARP). Calling getSession() validates the
    // session and lets propagateAuthCookies forward any Set-Cookie headers
    // from the API with the correct Domain attribute.
    const api = getApiClient();
    try {
      const session = await api.oidc.getSession();
      return { success: session?.isAuthenticated ?? false };
    } catch {
      return { success: false };
    }
  }
);
