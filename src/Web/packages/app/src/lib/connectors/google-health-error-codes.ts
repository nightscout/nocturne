/**
 * Every Google Health error code the connector page has a sentence for. Kept out of the
 * translated copy module so a server route can test a code without loading translations.
 */
const codes = [
  "configure_first",
  "platform_not_configured",
  "client_changed",
  "invalid_configuration",
  "invalid_callback",
  "invalid_client_credentials",
  "oauth_scope_configuration",
  "oauth_request_invalid",
  "invalid_token_response",
  "preview_required",
  "no_types_selected",
  "disconnect_first",
  "account_mismatch",
  "expired_signin",
  "offline_access_required",
  "partial_consent",
  "permission_denied",
  "reconnect_required",
  "rate_limited",
  "google_unavailable",
  "already_running",
  "account_not_linked",
  "invalid_google_request",
  "preview_access_denied",
  "google_resource_not_found",
  "data_access_denied",
  "invalid_time_range",
  "invalid_filter_operator",
  "invalid_google_filter",
  "invalid_google_data_type",
  "invalid_source_family",
  "invalid_google_response",
  "stored_google_configuration_unreadable",
  "unsupported_type",
  "revoke_in_google",
  "history_too_large",
  "invalid_google_data",
  "pagination_failed",
  "internal_sync",
  "internal_sync_native_write",
] as const;

export type GoogleHealthErrorCode = (typeof codes)[number];

const known: ReadonlySet<string> = new Set(codes);

export function isGoogleHealthErrorCode(code: string): code is GoogleHealthErrorCode {
  return known.has(code);
}
