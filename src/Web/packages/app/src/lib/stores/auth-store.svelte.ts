/**
 * Auth Store - Svelte 5 Runes-based store for OIDC authentication
 *
 * This store manages authentication state and provides reactive state
 * that can be shared across the application with support for:
 * - OIDC provider authentication
 * - Session management
 * - User info
 * - Token refresh
 */

import { getContext, setContext } from "svelte";
import { browser } from "$app/env";
import { goto } from "$app/navigation";
import { resolve } from "$app/paths";
import { AUTH_COOKIE_NAMES } from "#lib/config/auth-cookies.js";
import { toDate } from "#lib/utils/formatting.js";
import { toIsoString } from "#lib/utils/api-date.js";
import {
  getSessionInfo,
  refreshSession as refreshSessionRemote,
  logoutSession as logoutSessionRemote,
} from "../../routes/(unauthenticated)/auth/auth.remote";
import { remoteErrorMessage } from "#lib/api/remote-error.js";
import { whenIdle } from "#lib/utils/when-idle.js";
import { fresh } from "#lib/api/fresh-query.js";

const AUTH_STORE_KEY = "nocturne:app:auth-store";

const EXPIRY_CHECK_INTERVAL_MS = 30_000;

/**
 * How far ahead of expiry the session is refreshed. Kept well above the check
 * interval so a throttled background tab (Chrome clamps timers to once a
 * minute) still refreshes before the session lapses.
 */
const REFRESH_LEAD_MS = 3 * 60_000;

/**
 * User information from the current session
 */
export interface AuthUser {
  subjectId: string;
  name: string;
  email?: string;
  roles: string[];
  permissions: string[];
  /** ISO 8601. */
  expiresAt?: string;
  avatarUrl?: string;
}

/**
 * Session information from the API
 */
export interface SessionInfo {
  isAuthenticated: boolean;
  subjectId?: string;
  name?: string;
  email?: string;
  roles?: string[];
  permissions?: string[];
  expiresAt?: string;
  avatarUrl?: string;
}

export type AuthState = "idle" | "loading" | "authenticated" | "unauthenticated" | "error";

/**
 * Authentication Store class using Svelte 5 runes
 */
export class AuthStore {
  // Core state
  private _state = $state<AuthState>("idle");
  private _user = $state<AuthUser | null>(null);
  private _error = $state<string | null>(null);

  // Login dialog state
  private _loginDialogOpen = $state(false);

  // Derived state
  state = $derived(this._state);
  user = $derived(this._user);
  error = $derived(this._error);
  loginDialogOpen = $derived(this._loginDialogOpen);

  isAuthenticated = $derived(this._state === "authenticated" && this._user !== null);
  isLoading = $derived(this._state === "loading");
  hasError = $derived(this._state === "error");

  // Session expiry tracking
  private _expiresAt = $state<Date | null>(null);
  private expiryCheckInterval: ReturnType<typeof setInterval> | null = null;
  private refreshInFlight: Promise<boolean> | null = null;
  private cancelSessionLoad: (() => void) | null = null;

  expiresAt = $derived(this._expiresAt);

  constructor() {
    if (browser) {
      // Check for the IsAuthenticated cookie to determine initial state
      this.checkAuthCookie();

      // Start session expiry monitoring
      this.startExpiryCheck();
    }
  }

  /**
   * Check the IsAuthenticated cookie for quick client-side auth status
   */
  private checkAuthCookie(): void {
    if (!browser) return;

    const cookies = document.cookie.split(";");
    const authCookie = cookies.find((c) => c.trim().startsWith(`${AUTH_COOKIE_NAMES.isAuthenticated}=`));

    if (authCookie) {
      // Nothing on first paint reads the session (the layout's server data carries the user and
      // permissions), so it loads once the page is idle.
      this._state = "loading";
      this.cancelSessionLoad = whenIdle(() => {
        this.cancelSessionLoad = null;
        void this.loadSession();
      });
    } else {
      this._state = "unauthenticated";
    }
  }

  /**
   * Start monitoring session expiry
   */
  private startExpiryCheck(): void {
    if (!browser || this.expiryCheckInterval) return;

    this.expiryCheckInterval = setInterval(this.refreshIfExpiring, EXPIRY_CHECK_INTERVAL_MS);
    document.addEventListener("visibilitychange", this.refreshIfExpiring);
  }

  /**
   * Stop monitoring session expiry
   */
  private stopExpiryCheck(): void {
    if (this.expiryCheckInterval) {
      clearInterval(this.expiryCheckInterval);
      this.expiryCheckInterval = null;
    }
    if (browser) {
      document.removeEventListener("visibilitychange", this.refreshIfExpiring);
    }
  }

  private refreshIfExpiring = (): void => {
    if (!this._expiresAt || this._state !== "authenticated") return;
    if (document.visibilityState === "hidden") return;
    if (this._expiresAt.getTime() - Date.now() > REFRESH_LEAD_MS) return;
    void this.refreshSession();
  };

  /**
   * Load the current session from the API
   */
  async loadSession(): Promise<void> {
    if (!browser) return;

    this._state = "loading";
    this._error = null;

    try {
      const session = await fresh(getSessionInfo());

      if (session.isAuthenticated && session.subjectId) {
        this._user = {
          subjectId: session.subjectId,
          name: session.name ?? "User",
          email: session.email,
          roles: session.roles ?? [],
          permissions: session.permissions ?? [],
          expiresAt: toIsoString(session.expiresAt) ?? undefined,
          avatarUrl: session.avatarUrl,
        };
        this._expiresAt = toDate(session.expiresAt);
        this._state = "authenticated";
      } else {
        this._user = null;
        this._expiresAt = null;
        this._state = "unauthenticated";
      }
    } catch (e) {
      console.error("Failed to load session:", e);
      this._error = remoteErrorMessage(e, "Failed to load session");
      this._state = "unauthenticated";
      this._user = null;
      this._expiresAt = null;
    }
  }

  /**
   * Logout the current user
   * @param providerId - Optional provider ID for RP-initiated logout
   */
  async logout(providerId?: string): Promise<void> {
    if (!browser) return;

    this._state = "loading";

    try {
      const result = await logoutSessionRemote(providerId);

      // Clear local state
      this._user = null;
      this._expiresAt = null;
      this._state = "unauthenticated";

      if (!result.success) {
        console.warn("Logout API call failed, but local state cleared");
      }
    } catch (e) {
      console.error("Logout failed:", e);
    }

    // Clear state regardless of API result
    this._user = null;
    this._expiresAt = null;
    this._state = "unauthenticated";

    // Redirect to home
    goto(resolve(""));
  }

  /**
   * Refresh the current session
   */
  async refreshSession(): Promise<boolean> {
    if (!browser) return false;
    this.refreshInFlight ??= this.performRefresh().finally(() => {
      this.refreshInFlight = null;
    });
    return this.refreshInFlight;
  }

  private async performRefresh(): Promise<boolean> {
    try {
      const result = await refreshSessionRemote();

      if (result.success) {
        // Only the expiry moves. Reloading the session would pass through the
        // "loading" state and briefly unmount everything gated on isAuthenticated.
        this._expiresAt = toDate(result.expiresAt);
        return true;
      } else {
        // Refresh failed, user needs to re-login
        this._user = null;
        this._expiresAt = null;
        this._state = "unauthenticated";
        return false;
      }
    } catch (e) {
      console.error("Session refresh failed:", e);
      this._user = null;
      this._expiresAt = null;
      this._state = "unauthenticated";
      return false;
    }
  }

  /**
   * Request login from the user via the login dialog
   * Returns a promise that resolves when login completes (true) or is cancelled (false)
   */
  requestLogin(): Promise<boolean> {
    return new Promise<boolean>((resolve) => {
      // Store the resolver to be called when the dialog closes
      this._loginResolver = resolve;

      // Open the login dialog
      this._loginDialogOpen = true;
    });
  }

  /**
   * Login resolver for the promise returned by requestLogin()
   */
  private _loginResolver: ((success: boolean) => void) | null = null;

  /**
   * Handle login dialog close
   * @param success - Whether login was successful
   */
  handleLoginDialogClose(success: boolean): void {
    this._loginDialogOpen = false;

    if (this._loginResolver) {
      this._loginResolver(success);
      this._loginResolver = null;
    }
  }

  /**
   * Open the login dialog manually
   */
  openLoginDialog(): void {
    this._loginDialogOpen = true;
  }

  /**
   * Close the login dialog manually
   */
  closeLoginDialog(): void {
    this._loginDialogOpen = false;
    if (this._loginResolver) {
      this._loginResolver(false);
      this._loginResolver = null;
    }
  }

  /**
   * Update the avatar URL after upload or deletion
   */
  updateAvatarUrl(url: string | undefined): void {
    if (this._user) {
      this._user = { ...this._user, avatarUrl: url };
    }
  }

  /**
   * Cleanup when the store is destroyed
   */
  destroy(): void {
    this.cancelSessionLoad?.();
    this.cancelSessionLoad = null;
    this.stopExpiryCheck();
    this.closeLoginDialog();
  }
}

/**
 * Create an auth store and set it in context
 */
export function createAuthStore(): AuthStore {
  const store = new AuthStore();
  setContext(AUTH_STORE_KEY, store);
  return store;
}

/**
 * Get the auth store from context
 */
export function getAuthStore(): AuthStore {
  const store = getContext<AuthStore>(AUTH_STORE_KEY);
  if (!store) {
    throw new Error(
      "Auth store not found in context. Make sure createAuthStore() is called in a parent component."
    );
  }
  return store;
}

/**
 * Format session expiry time for display
 */
export function formatSessionExpiry(seconds: number | null): string {
  if (seconds === null || seconds <= 0) return "Expired";

  if (seconds < 60) {
    return `${seconds}s`;
  }

  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) {
    return `${minutes}m`;
  }

  const hours = Math.floor(minutes / 60);
  const remainingMinutes = minutes % 60;
  if (hours < 24) {
    return remainingMinutes > 0 ? `${hours}h ${remainingMinutes}m` : `${hours}h`;
  }

  const days = Math.floor(hours / 24);
  const remainingHours = hours % 24;
  return remainingHours > 0 ? `${days}d ${remainingHours}h` : `${days}d`;
}
