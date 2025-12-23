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
import { browser } from "$app/environment";
import { goto } from "$app/navigation";
import {
  getSessionInfo,
  getProvidersInfo,
  refreshSession as refreshSessionRemote,
  logoutSession as logoutSessionRemote,
} from "../../routes/auth/auth.remote";

const AUTH_STORE_KEY = Symbol("auth-store");

/**
 * User information from the current session
 */
export interface AuthUser {
  subjectId: string;
  name: string;
  email?: string;
  roles: string[];
  permissions: string[];
  expiresAt?: Date;
}

/**
 * OIDC Provider information for login UI
 */
export interface OidcProvider {
  id: string;
  name: string;
  icon?: string;
  buttonColor?: string;
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
  private _providers = $state<OidcProvider[]>([]);

  // Derived state
  state = $derived(this._state);
  user = $derived(this._user);
  error = $derived(this._error);
  providers = $derived(this._providers);

  isAuthenticated = $derived(this._state === "authenticated" && this._user !== null);
  isLoading = $derived(this._state === "loading");
  hasError = $derived(this._state === "error");

  // Session expiry tracking
  private _expiresAt = $state<Date | null>(null);
  private _expiryWarningShown = $state(false);
  private expiryCheckInterval: ReturnType<typeof setInterval> | null = null;

  expiresAt = $derived(this._expiresAt);
  expiryWarningShown = $derived(this._expiryWarningShown);

  /**
   * Time until session expires in seconds (null if not authenticated)
   */
  timeUntilExpiry = $derived.by(() => {
    if (!this._expiresAt) return null;
    const now = new Date();
    const diff = this._expiresAt.getTime() - now.getTime();
    return Math.max(0, Math.floor(diff / 1000));
  });

  /**
   * Whether the session is about to expire (within 5 minutes)
   */
  isSessionExpiringSoon = $derived.by(() => {
    const timeLeft = this.timeUntilExpiry;
    return timeLeft !== null && timeLeft > 0 && timeLeft < 300;
  });

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
    const authCookie = cookies.find((c) => c.trim().startsWith("IsAuthenticated="));

    if (authCookie) {
      // We have an auth cookie, try to load the full session
      this._state = "loading";
      this.loadSession();
    } else {
      this._state = "unauthenticated";
    }
  }

  /**
   * Start monitoring session expiry
   */
  private startExpiryCheck(): void {
    if (!browser || this.expiryCheckInterval) return;

    this.expiryCheckInterval = setInterval(() => {
      if (this._expiresAt && this._state === "authenticated") {
        const now = new Date();
        if (now >= this._expiresAt) {
          // Session expired, trigger refresh
          this.refreshSession();
        } else if (!this._expiryWarningShown && this.isSessionExpiringSoon) {
          this._expiryWarningShown = true;
          // Session expiring soon, could trigger a notification here
        }
      }
    }, 30000); // Check every 30 seconds
  }

  /**
   * Stop monitoring session expiry
   */
  private stopExpiryCheck(): void {
    if (this.expiryCheckInterval) {
      clearInterval(this.expiryCheckInterval);
      this.expiryCheckInterval = null;
    }
  }

  /**
   * Load the current session from the API
   */
  async loadSession(): Promise<void> {
    if (!browser) return;

    this._state = "loading";
    this._error = null;

    try {
      const session = await getSessionInfo();

      if (session.isAuthenticated && session.subjectId) {
        this._user = {
          subjectId: session.subjectId,
          name: session.name ?? "User",
          email: session.email,
          roles: session.roles ?? [],
          permissions: session.permissions ?? [],
          expiresAt: session.expiresAt ? new Date(session.expiresAt) : undefined,
        };
        this._expiresAt = session.expiresAt ? new Date(session.expiresAt) : null;
        this._state = "authenticated";
        this._expiryWarningShown = false;
      } else {
        this._user = null;
        this._expiresAt = null;
        this._state = "unauthenticated";
      }
    } catch (e) {
      console.error("Failed to load session:", e);
      this._error = e instanceof Error ? e.message : "Failed to load session";
      this._state = "unauthenticated";
      this._user = null;
      this._expiresAt = null;
    }
  }

  /**
   * Load available OIDC providers
   */
  async loadProviders(): Promise<OidcProvider[]> {
    if (!browser) return [];

    try {
      const result = await getProvidersInfo();
      this._providers = result.providers.map((p) => ({
        id: p.id ?? "",
        name: p.name ?? "",
        icon: p.icon,
        buttonColor: p.buttonColor,
      }));

      return this._providers;
    } catch (e) {
      console.error("Failed to load providers:", e);
      return [];
    }
  }

  /**
   * Initiate OIDC login flow
   * @param providerId - Optional provider ID (uses default if not specified)
   * @param returnUrl - URL to return to after login
   */
  login(providerId?: string, returnUrl?: string): void {
    if (!browser) return;

    const params = new URLSearchParams();
    if (providerId) {
      params.set("provider", providerId);
    }
    if (returnUrl) {
      params.set("returnUrl", returnUrl);
    }

    const loginUrl = `/api/auth/login${params.toString() ? `?${params.toString()}` : ""}`;
    window.location.href = loginUrl;
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
      this._expiryWarningShown = false;

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
    goto("/");
  }

  /**
   * Refresh the current session
   */
  async refreshSession(): Promise<boolean> {
    if (!browser) return false;

    try {
      const result = await refreshSessionRemote();

      if (result.success) {
        this._expiresAt = result.expiresAt ? new Date(result.expiresAt) : null;
        this._expiryWarningShown = false;

        // Reload session to get updated user info
        await this.loadSession();
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
   * Check if the user has a specific permission
   * @param permission - Shiro-style permission string (e.g., "api:entries:read")
   */
  hasPermission(permission: string): boolean {
    if (!this._user) return false;

    // Check for admin wildcard
    if (this._user.permissions.includes("*")) return true;

    // Check for exact match
    if (this._user.permissions.includes(permission)) return true;

    // Check for wildcard matches (e.g., "api:entries:*" matches "api:entries:read")
    const parts = permission.split(":");
    for (const userPerm of this._user.permissions) {
      const userParts = userPerm.split(":");

      // Skip if user permission is longer than the requested permission
      if (userParts.length > parts.length) continue;

      let matches = true;
      for (let i = 0; i < userParts.length; i++) {
        if (userParts[i] === "*") {
          // Wildcard matches everything at this level and below
          return true;
        }
        if (userParts[i] !== parts[i]) {
          matches = false;
          break;
        }
      }

      if (matches && userParts.length === parts.length) return true;
    }

    return false;
  }

  /**
   * Check if the user has a specific role
   * @param role - Role name (e.g., "admin", "readable")
   */
  hasRole(role: string): boolean {
    if (!this._user) return false;
    return this._user.roles.includes(role);
  }

  /**
   * Check if the user has any of the specified roles
   * @param roles - Array of role names
   */
  hasAnyRole(roles: string[]): boolean {
    if (!this._user) return false;
    return roles.some((role) => this._user!.roles.includes(role));
  }

  /**
   * Dismiss the session expiry warning
   */
  dismissExpiryWarning(): void {
    this._expiryWarningShown = false;
  }

  /**
   * Cleanup when the store is destroyed
   */
  destroy(): void {
    this.stopExpiryCheck();
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
