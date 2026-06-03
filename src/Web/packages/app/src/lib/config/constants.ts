/**
 * Hardcoded frontend constants.
 *
 * These values used to be environment variables but were moved here because
 * they are internal tuning / build-time identity, not per-deployment config.
 * If a self-hoster ever has a legitimate reason to change one, promote it
 * back to env — but the default should be "live in source".
 */

// WebSocket bridge tuning (server.js + @nocturne/bridge)
export const WEBSOCKET_RECONNECT_ATTEMPTS = 5;
export const WEBSOCKET_RECONNECT_DELAY_MS = 1_000;
export const WEBSOCKET_MAX_RECONNECT_DELAY_MS = 30_000;
export const WEBSOCKET_PING_TIMEOUT_MS = 15_000;
export const WEBSOCKET_PING_INTERVAL_MS = 20_000;

// Auth cookie names. Only matters under cookie-domain collisions; revisit
// if anyone reports a conflict.
export const COOKIE_ACCESS_TOKEN_NAME = ".Nocturne.AccessToken";
export const COOKIE_REFRESH_TOKEN_NAME = ".Nocturne.RefreshToken";
// Platform-admin tenant-access grant (short-lived, tenant-pinned). Must be forwarded
// to the API alongside the session so out-of-tenant superuser requests carry it.
export const COOKIE_PLATFORM_ACCESS_NAME = ".Nocturne.PlatformAccess";

// OpenTelemetry service identity (build-time, not deployment config)
export const OTEL_SERVICE_NAME = "nocturne-web";
