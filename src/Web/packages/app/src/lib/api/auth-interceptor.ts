/**
 * Authentication Interceptor for API Client
 *
 * Intercepts 401 Unauthorized responses and redirects to login page.
 */

import { browser } from "$app/env";
import { safeReturnUrl } from "#lib/navigation/safe-return-url.js";
import { isShareHost } from "#lib/share-host.js";

const REMOTE_CALL_PATH = "/_app/remote/";
const SELF_ANSWERING_PAGES = ["/auth/login", "/auth/recovery"];

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

    // Public share-host viewers are anonymous by design: the tenant shares only some data
    // categories, so a 401 on an unshared one is expected and must not bounce the viewer to
    // login — they have no account to sign into. Mirrors the guest-session exemption above.
    if (isShareHost(window.location.hostname)) {
      return;
    }

    // The sign-in and recovery pages answer their own 401: a recovery session that ran out, a
    // spent enrolment or a refused code. Other /auth/ pages (linking a bot account) hold a session.
    if (SELF_ANSWERING_PAGES.some((page) => window.location.pathname.startsWith(page))) {
      return;
    }

    this.isRedirecting = true;

    const returnUrl = safeReturnUrl(window.location.pathname + window.location.search);
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

/**
 * Sends the person to sign in when the server refuses a remote command or form with 401.
 *
 * `redirect` is not allowed in a command, so the generated 401 arm throws `error(401)`, which
 * SvelteKit answers with a 200 whose body carries the status, and rejects at the call site.
 * Every command and form posts through the global `fetch`, so this is the one place that sees the
 * refusal for all of them. A query is sent with GET and redirects on the server.
 */
export function createRemoteSignInFetch(originalFetch: typeof fetch): typeof fetch {
  return async (input, init) => {
    const response = await originalFetch(input, init);
    if (init?.method === "POST" && isRemoteCall(input) && (await refusedAsUnauthorized(response))) {
      authInterceptorState.redirectToLogin();
    }
    return response;
  };
}

function isRemoteCall(input: RequestInfo | URL): boolean {
  const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
  return new URL(url, window.location.href).pathname.startsWith(REMOTE_CALL_PATH);
}

async function refusedAsUnauthorized(response: Response): Promise<boolean> {
  const body: unknown = await response.clone().json().catch(() => null);
  if (typeof body !== "object" || body === null || !("type" in body) || body.type !== "error") return false;
  const error = "error" in body ? body.error : null;
  return typeof error === "object" && error !== null && "status" in error && error.status === 401;
}
