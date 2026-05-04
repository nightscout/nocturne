/**
 * Authentication Interceptor for API Client
 *
 * Intercepts 401 Unauthorized responses and redirects to login page.
 */

import { browser } from "$app/environment";

/**
 * Auth interceptor state
 */
class AuthInterceptorState {
  private isRedirecting = false;
  private _isGuestSession = false;

  /**
   * Mark the current session as a guest session.
   * Guest sessions should not redirect to login on 401 — the guest has
   * limited scopes and some endpoints are expected to reject access.
   */
  setGuestSession(isGuest: boolean): void {
    this._isGuestSession = isGuest;
  }

  get isGuestSession(): boolean {
    return this._isGuestSession;
  }

  /**
   * Redirect to login page with return URL
   */
  redirectToLogin(): void {
    if (this.isRedirecting || this._isGuestSession) {
      return;
    }

    if (!browser) {
      return;
    }

    // Don't redirect if already on the login page
    if (window.location.pathname.startsWith('/auth/login')) {
      return;
    }

    this.isRedirecting = true;

    const returnUrl = window.location.pathname + window.location.search;
    window.location.href = `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}`;
  }

  /**
   * Reset the interceptor state (for testing)
   */
  reset(): void {
    this.isRedirecting = false;
    this._isGuestSession = false;
  }
}

// Singleton instance
export const authInterceptorState = new AuthInterceptorState();

/**
 * Create an authenticated fetch wrapper that handles 401 responses
 */
export function createAuthenticatedFetch(
  originalFetch: (url: RequestInfo, init?: RequestInit) => Promise<Response>
): (url: RequestInfo, init?: RequestInit) => Promise<Response> {
  return async (url: RequestInfo, init?: RequestInit): Promise<Response> => {
    // Make the initial request
    const response = await originalFetch(url, init);

    // If not a 401, return the response as-is
    if (response.status !== 401) {
      return response;
    }

    // Skip auth flow for auth endpoints themselves (prevent infinite loops)
    const urlString = typeof url === "string" ? url : url.toString();
    if (
      urlString.includes("/api/auth/") ||
      urlString.includes("/api/local-auth/")
    ) {
      return response;
    }

    // Only handle 401 in browser context
    if (!browser) {
      return response;
    }

    console.log("[Auth Interceptor] 401 detected, redirecting to login");

    // Redirect to login page
    authInterceptorState.redirectToLogin();

    // Return the 401 response (page will redirect before this matters)
    return response;
  };
}
