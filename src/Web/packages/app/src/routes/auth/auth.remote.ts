/**
 * Authentication Remote Functions
 *
 * Server-side functions for handling authentication using SvelteKit's remote functions.
 * These use Zod for validation and the API client for backend communication.
 */

import { z } from "zod";
import { query, form, getRequestEvent } from "$app/server";

import { redirect, invalid } from "@sveltejs/kit";
import type { LoginResponse, OidcProviderInfo, RegisterResponse } from "$lib/api/generated/nocturne-api-client";

// ============================================================================
// Zod Schemas
// ============================================================================

const emailSchema = z.email("Please enter a valid email address");

const passwordSchema = z
  .string()
  .min(12, "Password must be at least 12 characters");

const loginSchema = z.object({
  email: emailSchema,
  _password: passwordSchema,
  returnUrl: z.string().optional().default("/"),
});

const registerSchema = z.object({
  email: emailSchema,
  _password: passwordSchema,
  confirmPassword: z.string(),
  displayName: z.string().optional(),
  returnUrl: z.string().optional().default("/"),
});

const forgotPasswordSchema = z.object({
  email: emailSchema,
});

const resetPasswordSchema = z.object({
  token: z.string().min(1, "Reset token is required"),
  _password: passwordSchema,
  confirmPassword: z.string(),
});

const verifyEmailSchema = z.object({
  token: z.string().min(1, "Verification token is required"),
});

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

/**
 * Parse API error response for user-friendly message
 */
function parseApiError(error: unknown): string {
  if (error instanceof Error) {
    // Check for API exception with response body
    const apiError = error as Error & { response?: string; status?: number };
    if (apiError.response) {
      try {
        const parsed = JSON.parse(apiError.response);
        return parsed.message || parsed.error || "An error occurred";
      } catch {
        return apiError.response;
      }
    }
    return error.message;
  }
  return "An unexpected error occurred";
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
 * Get local authentication configuration
 * Returns whether local auth is enabled and its settings
 */
export const getLocalAuthConfig = query(async () => {
  const result = await safeApiCall(async () => {
    const api = getApiClient();
    const config = await api.localAuth.getConfig();
    return config;
  });

  // Return safe defaults if API is unavailable
  return (
    result ?? {
      enabled: false,
      displayName: "Nocturne",
      allowRegistration: false,
      requireEmailVerification: false,
      passwordRequirements: {
        minLength: 12,
        requireUppercase: false,
        requireLowercase: false,
        requireDigit: false,
        requireSpecialCharacter: false,
      },
    }
  );
});

// ============================================================================
// Form Functions
// ============================================================================

/**
 * Login form handler
 * Authenticates user with email and password
 */
export const loginForm = form(loginSchema, async (data, issue) => {
  const api = getApiClient();
  const event = getRequestEvent();

  if (!event) {
    throw new Error("Request event not available");
  }
  let loginSucceeded = false;

  try {
    const response = await api.localAuth.login({
      email: data.email,
      password: data._password,
    });

    if (!response.success) {
      invalid(issue("Invalid email or password"));
      return;
    }

    // Set auth cookies on the SvelteKit response
    // The backend returns tokens in the response body, we need to set them as cookies
    // so the browser will send them on subsequent requests
    const isSecure = event.url.protocol === "https:";

    if (response.accessToken) {
      event.cookies.set(".Nocturne.AccessToken", response.accessToken, {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: response.expiresIn || 3600, // Default 1 hour
      });
    }

    if (response.refreshToken) {
      event.cookies.set(".Nocturne.RefreshToken", response.refreshToken, {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: 60 * 60 * 24 * 7, // 7 days
      });
    }

    // Set a non-HttpOnly cookie for client-side auth state checking
    event.cookies.set("IsAuthenticated", "true", {
      path: "/",
      httpOnly: false,
      secure: isSecure,
      sameSite: "lax",
      maxAge: response.expiresIn || 3600,
    });
    loginSucceeded = true;
  } catch (error) {
    const message = parseApiError(error);

    // Check for specific error codes
    if (message.includes("locked")) {
      invalid(
        issue(
          "Your account has been temporarily locked due to too many failed attempts. Please try again later."
        )
      );
    } else if (message.includes("not verified")) {
      invalid(
        issue(
          "Please verify your email address before logging in. Check your inbox for a verification link."
        )
      );
    } else if (message.includes("not active")) {
      invalid(
        issue(
          "Your account is not active. Please contact an administrator for assistance."
        )
      );
    } else {
      invalid(issue("Invalid email or password"));
    }
  }
  if (loginSucceeded) {
    redirect(303, data.returnUrl || "/");
  }
});

/**
 * Registration form handler
 * Creates a new user account
 */
export const registerForm = form(registerSchema, async (data, issue) => {
  // Validate passwords match
  if (data._password !== data.confirmPassword) {
    invalid(issue.confirmPassword("Passwords do not match"));
    return;
  }

  const api = getApiClient();

  try {
    const response = await api.localAuth.register({
      email: data.email,
      password: data._password,
      displayName: data.displayName || undefined,
    });

    if (!response.success) {
      invalid(issue("Registration failed. Please try again."));
      return;
    }

    // Return the response so the component can check requiresEmailVerification
    return response;
  } catch (error) {
    const message = parseApiError(error);

    if (message.includes("already exists")) {
      invalid(
        issue.email("An account with this email address already exists")
      );
    } else if (message.includes("not allowed")) {
      invalid(
        issue.email("This email address is not allowed to register")
      );
    } else if (message.includes("registration") && message.includes("disabled")) {
      invalid(issue("Registration is currently disabled"));
    } else if (message.includes("password")) {
      invalid(issue._password(message));
    } else {
      invalid(issue("Registration failed: " + message));
    }
  }
});

/**
 * Forgot password form handler
 * Initiates password reset flow
 */
export const forgotPasswordForm = form(
  forgotPasswordSchema,
  async (data, _issue) => {
    const api = getApiClient();

    try {
      const response = await api.localAuth.forgotPassword({
        email: data.email,
      });

      // Always return success to prevent email enumeration
      return {
        success: true,
        message:
          response.message ||
          "If an account exists with this email, you will receive a password reset link.",
        adminNotificationRequired: response.adminNotificationRequired ?? false,
      };
    } catch (error) {
      // Still return success to prevent email enumeration
      // But log the actual error server-side
      console.error("Forgot password error:", error);

      return {
        success: true,
        message:
          "If an account exists with this email, you will receive a password reset link.",
        adminNotificationRequired: false,
      };
    }
  }
);

/**
 * Reset password form handler
 * Completes password reset with token
 */
export const resetPasswordForm = form(
  resetPasswordSchema,
  async (data, issue) => {
    // Validate passwords match
    if (data._password !== data.confirmPassword) {
      invalid(issue.confirmPassword("Passwords do not match"));
      return;
    }

    const api = getApiClient();

    try {
      const response = await api.localAuth.resetPassword({
        token: data.token,
        newPassword: data._password,
      });

      if (!response.success) {
        invalid(issue("Failed to reset password. The link may have expired."));
        return;
      }

      return {
        success: true,
        message: response.message || "Your password has been reset successfully.",
      };
    } catch (error) {
      const message = parseApiError(error);

      if (message.includes("expired") || message.includes("invalid")) {
        invalid(
          issue(
            "This password reset link has expired or is invalid. Please request a new one."
          )
        );
      } else if (message.includes("password")) {
        invalid(issue._password(message));
      } else {
        invalid(issue("Failed to reset password: " + message));
      }
    }
  }
);

/**
 * Verify email form handler
 * Verifies email address with token
 */
export const verifyEmailForm = form(verifyEmailSchema, async (data, issue) => {
  const api = getApiClient();

  try {
    await api.localAuth.verifyEmail(data.token);

    return {
      success: true,
      message: "Your email has been verified successfully. You can now log in.",
    };
  } catch (error) {
    const message = parseApiError(error);

    if (message.includes("expired") || message.includes("invalid")) {
      invalid(
        issue(
          "This verification link has expired or is invalid. Please request a new one."
        )
      );
    } else if (message.includes("already verified")) {
      return {
        success: true,
        message: "Your email has already been verified. You can log in.",
      };
    } else {
      invalid(issue("Failed to verify email: " + message));
    }
  }
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
export const refreshSession = query(async () => {
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

      event.cookies.set(".Nocturne.AccessToken", result.accessToken, {
        path: "/",
        httpOnly: true,
        secure: isSecure,
        sameSite: "lax",
        maxAge: result.expiresIn || 3600,
      });

      event.cookies.set("IsAuthenticated", "true", {
        path: "/",
        httpOnly: false,
        secure: isSecure,
        sameSite: "lax",
        maxAge: result.expiresIn || 3600,
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
export const logoutSession = query(z.string().optional(), async (_providerId) => {
  const event = getRequestEvent();
  if (!event) {
    return { success: false };
  }

  const api = getApiClient();

  try {
    // Try to revoke on the backend
    await api.oidc.logout();

    // Clear all auth cookies
    event.cookies.delete(".Nocturne.AccessToken", { path: "/" });
    event.cookies.delete(".Nocturne.RefreshToken", { path: "/" });
    event.cookies.delete("IsAuthenticated", { path: "/" });

    return { success: true };
  } catch (error) {
    console.error("Failed to logout:", error);

    // Still clear cookies even if backend call fails
    event.cookies.delete(".Nocturne.AccessToken", { path: "/" });
    event.cookies.delete(".Nocturne.RefreshToken", { path: "/" });
    event.cookies.delete("IsAuthenticated", { path: "/" });

    return { success: true };
  }
});

// ============================================================================
// Type Exports for Components
// ============================================================================

export type LoginFormResult = LoginResponse;
export type RegisterFormResult = RegisterResponse;
export type ForgotPasswordFormResult = {
  success: boolean;
  message: string;
  adminNotificationRequired: boolean;
};
export type ResetPasswordFormResult = {
  success: boolean;
  message: string;
};
export type VerifyEmailFormResult = {
  success: boolean;
  message: string;
};
