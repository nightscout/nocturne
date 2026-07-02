//! Shared reqwest client construction.

/// Builds the app's HTTP client. Invalid certs are accepted in debug builds only — local dev runs
/// behind the Aspire gateway's self-signed certificate.
pub fn client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .danger_accept_invalid_certs(cfg!(debug_assertions))
        .build()
        .map_err(|e| format!("could not create HTTP client: {e}"))
}
