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
  isAuthenticated: "IsAuthenticated",
} as const;
