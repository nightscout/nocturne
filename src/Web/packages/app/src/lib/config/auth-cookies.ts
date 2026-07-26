/**
 * Authentication Cookie Configuration
 *
 * Cookie names are hardcoded constants. They previously came from env so they
 * could match the API's Oidc:Cookie configuration, but in practice the API
 * never overrode them — both sides just used the defaults.
 */

import {
  COOKIE_ACCESS_TOKEN_NAME,
  COOKIE_REFRESH_TOKEN_NAME,
  COOKIE_PLATFORM_ACCESS_NAME,
  COOKIE_GUEST_SESSION_NAME,
} from "./constants";

export function getAccessTokenCookieName(): string {
  return COOKIE_ACCESS_TOKEN_NAME;
}

export function getRefreshTokenCookieName(): string {
  return COOKIE_REFRESH_TOKEN_NAME;
}

export const AUTH_COOKIE_NAMES = {
  accessToken: COOKIE_ACCESS_TOKEN_NAME,
  refreshToken: COOKIE_REFRESH_TOKEN_NAME,
  platformAccess: COOKIE_PLATFORM_ACCESS_NAME,
  guestSession: COOKIE_GUEST_SESSION_NAME,
  isAuthenticated: "IsAuthenticated",
} as const;

/**
 * Every cookie that carries session identity. `authHandle` falls back to the
 * guest cookie when both token cookies are absent, so a logout that leaves the
 * guest cookie (or a platform-access grant) behind signs the visitor straight
 * back in.
 */
const SESSION_COOKIE_NAMES = [
  AUTH_COOKIE_NAMES.accessToken,
  AUTH_COOKIE_NAMES.refreshToken,
  AUTH_COOKIE_NAMES.isAuthenticated,
  AUTH_COOKIE_NAMES.guestSession,
  AUTH_COOKIE_NAMES.platformAccess,
] as const;

/** Minimal shape of SvelteKit's `Cookies`, so this stays testable. */
export interface CookieDeleter {
  delete(name: string, opts: { path: string }): void;
}

/** Clears every session cookie. Used by both logout entry points. */
export function clearAuthCookies(cookies: CookieDeleter): void {
  for (const name of SESSION_COOKIE_NAMES) {
    cookies.delete(name, { path: "/" });
  }
}
