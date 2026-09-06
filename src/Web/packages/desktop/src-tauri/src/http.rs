//! Shared reqwest client construction.

/// Builds the app's HTTP client. Invalid certs are accepted in debug builds only — local dev runs
/// behind the Aspire gateway's self-signed certificate.
pub fn client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .danger_accept_invalid_certs(cfg!(debug_assertions))
        // Trust the OS certificate store in addition to the bundled roots, so release builds
        // behind TLS-intercepting antivirus/VPN/proxies (which curl and browsers accept via the
        // Windows store) don't fail every request with an opaque transport error.
        .tls_built_in_native_certs(true)
        // Bound each request so a stalled connect (dead network, dropped route, TLS hang)
        // fails fast and lets the 60s poll loop retry, rather than parking on the OS SYN
        // timeout.
        .connect_timeout(std::time::Duration::from_secs(10))
        .timeout(std::time::Duration::from_secs(30))
        .build()
        .map_err(|e| format!("could not create HTTP client: {e}"))
}
