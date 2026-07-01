#![allow(dead_code)]

//! Durable OAuth auth for the companion's glucose poller.
//!
//! Ports the Windows 11 widget's `OAuthService` pattern (src/Widgets/.../OAuthService.cs):
//! RFC 7591 dynamic client registration, RFC 8628 device-authorization grant, refresh-token
//! rotation, and credential persistence to the Windows Credential Manager. Read-scoped
//! (`glucose.read therapy.read devices.read`).
//!
//! Separate from the CareLink link-code JWT in `main.rs`, which is a one-shot connect credential;
//! this module owns the long-lived credential the poll loop runs on.

use serde::{Deserialize, Serialize};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const DEFAULT_SCOPES: &str = "glucose.read therapy.read devices.read";
const CLIENT_NAME: &str = "Nocturne Companion";
const SOFTWARE_ID: &str = "nocturne-companion";
const CLIENT_URI: &str = "https://github.com/nightscout/nocturne";
// The device flow never redirects, but RFC 7591 registration requires a redirect URI.
const REDIRECT_URI: &str = "com.nocturne.companion://oauth/callback";

const CRED_TARGET: &str = "Nocturne.Companion.OAuth";
const CRED_USER: &str = "default";

// Refresh this many seconds before the access token expires.
const REFRESH_SKEW_SECS: i64 = 60;

// Serialized as the Credential Manager secret.
#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
struct StoredCreds {
    api_url: String,
    client_id: String,
    access_token: String,
    refresh_token: String,
    expires_at_unix: i64,
    scopes: Vec<String>,
}

// OAuth wire models — snake_case per RFC, matching the server's JsonPropertyName.
#[derive(Deserialize)]
struct RegisterResponse {
    client_id: String,
}

#[derive(Deserialize)]
struct DeviceAuthResponse {
    device_code: String,
    user_code: String,
    verification_uri: String,
    verification_uri_complete: Option<String>,
    expires_in: i64,
    interval: Option<i64>,
}

#[derive(Deserialize)]
struct TokenResponse {
    access_token: String,
    refresh_token: Option<String>,
    expires_in: i64,
    scope: Option<String>,
}

#[derive(Deserialize)]
struct OAuthError {
    error: String,
    error_description: Option<String>,
}

/// Shown to the user to complete the device-authorization ceremony.
#[derive(Serialize, Clone)]
#[serde(rename_all = "camelCase")]
pub struct DeviceFlowInfo {
    pub user_code: String,
    pub verification_uri: String,
    pub verification_uri_complete: Option<String>,
    pub interval_secs: u64,
    pub expires_in_secs: i64,
}

/// Carries what `await_authorization` needs to poll the token endpoint.
pub struct PendingAuth {
    api_url: String,
    client_id: String,
    device_code: String,
    interval_secs: u64,
    deadline_unix: i64,
}

fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

/// Begins the device flow: registers a client (idempotent per tenant) and requests a device
/// code. Returns the info to display plus a `PendingAuth` to hand to `await_authorization`.
pub async fn begin_device_flow(
    client: &reqwest::Client,
    api_url: &str,
) -> Result<(DeviceFlowInfo, PendingAuth), String> {
    let api_url = api_url.trim_end_matches('/').to_string();
    let client_id = register_client(client, &api_url).await?;

    let resp = client
        .post(format!("{api_url}/api/oauth/device"))
        .form(&[("client_id", client_id.as_str()), ("scope", DEFAULT_SCOPES)])
        .send()
        .await
        .map_err(|e| format!("Could not reach {api_url}: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("Device authorization failed ({}).", oauth_error(resp).await));
    }

    let body: DeviceAuthResponse = resp
        .json()
        .await
        .map_err(|e| format!("Unexpected device-authorization response: {e}"))?;

    let interval_secs = body.interval.unwrap_or(5).max(1) as u64;
    let info = DeviceFlowInfo {
        user_code: body.user_code,
        verification_uri: body.verification_uri,
        verification_uri_complete: body.verification_uri_complete,
        interval_secs,
        expires_in_secs: body.expires_in,
    };
    let pending = PendingAuth {
        api_url,
        client_id,
        device_code: body.device_code,
        interval_secs,
        deadline_unix: now_unix() + body.expires_in,
    };
    Ok((info, pending))
}

/// Polls the token endpoint until the user approves (or the code expires / is denied). On
/// success the access + refresh tokens are persisted to the Credential Manager.
pub async fn await_authorization(
    client: &reqwest::Client,
    pending: PendingAuth,
) -> Result<(), String> {
    let mut interval = pending.interval_secs;
    loop {
        if now_unix() >= pending.deadline_unix {
            return Err("The sign-in code expired before it was approved.".to_string());
        }
        tokio::time::sleep(Duration::from_secs(interval)).await;

        let resp = client
            .post(format!("{}/api/oauth/token", pending.api_url))
            .form(&[
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("device_code", pending.device_code.as_str()),
                ("client_id", pending.client_id.as_str()),
            ])
            .send()
            .await
            .map_err(|e| format!("Could not reach the server: {e}"))?;

        if resp.status().is_success() {
            let token: TokenResponse = resp
                .json()
                .await
                .map_err(|e| format!("Unexpected token response: {e}"))?;
            let creds = StoredCreds {
                api_url: pending.api_url.clone(),
                client_id: pending.client_id.clone(),
                access_token: token.access_token,
                refresh_token: token.refresh_token.unwrap_or_default(),
                expires_at_unix: now_unix() + token.expires_in,
                scopes: token
                    .scope
                    .map(|s| s.split_whitespace().map(str::to_string).collect())
                    .unwrap_or_default(),
            };
            store(&creds)?;
            return Ok(());
        }

        // Non-2xx: interpret the standard device-flow error codes.
        match oauth_error_code(resp).await.as_str() {
            "authorization_pending" => {}
            "slow_down" => interval += 5,
            "access_denied" => return Err("The sign-in request was denied.".to_string()),
            "expired_token" => return Err("The sign-in code expired.".to_string()),
            other => return Err(format!("Sign-in failed ({other}).")),
        }
    }
}

/// Returns `(api_url, access_token)` for the poller, refreshing the access token first if it is
/// within `REFRESH_SKEW_SECS` of expiry. Errors if there are no stored credentials (the user
/// must link) or the refresh fails (re-link needed).
pub async fn get_valid_token(client: &reqwest::Client) -> Result<(String, String), String> {
    let creds = load().ok_or("Not linked to a Nocturne server yet.")?;
    if now_unix() < creds.expires_at_unix - REFRESH_SKEW_SECS {
        return Ok((creds.api_url, creds.access_token));
    }
    if creds.refresh_token.is_empty() {
        return Err("Session expired and there is no refresh token; please link again.".to_string());
    }

    let resp = client
        .post(format!("{}/api/oauth/token", creds.api_url))
        .form(&[
            ("grant_type", "refresh_token"),
            ("refresh_token", creds.refresh_token.as_str()),
            ("client_id", creds.client_id.as_str()),
        ])
        .send()
        .await
        .map_err(|e| format!("Could not reach {}: {e}", creds.api_url))?;

    if !resp.status().is_success() {
        return Err(format!("Token refresh failed ({}); please link again.", oauth_error(resp).await));
    }

    let token: TokenResponse = resp
        .json()
        .await
        .map_err(|e| format!("Unexpected refresh response: {e}"))?;

    let refreshed = StoredCreds {
        api_url: creds.api_url.clone(),
        client_id: creds.client_id,
        access_token: token.access_token.clone(),
        // The refresh token rotates server-side; keep the old one if none is returned.
        refresh_token: token.refresh_token.unwrap_or(creds.refresh_token),
        expires_at_unix: now_unix() + token.expires_in,
        scopes: token
            .scope
            .map(|s| s.split_whitespace().map(str::to_string).collect())
            .unwrap_or(creds.scopes),
    };
    store(&refreshed)?;
    Ok((refreshed.api_url, token.access_token))
}

pub fn is_linked() -> bool {
    load().is_some()
}

/// The linked server's base URL, if any. The floating clock window uses it to build the public
/// clock URL (`{server}/clock/{id}`); returns `None` when the companion isn't linked yet.
pub fn server_url() -> Option<String> {
    load().map(|c| c.api_url)
}

/// Removes the stored credential (unlink).
pub fn clear() -> Result<(), String> {
    match keyring_entry()?.delete_credential() {
        Ok(()) => Ok(()),
        Err(keyring::Error::NoEntry) => Ok(()),
        Err(e) => Err(format!("Could not clear stored credentials: {e}")),
    }
}

/// RFC 7591 dynamic client registration. Idempotent per tenant on `software_id`.
async fn register_client(client: &reqwest::Client, api_url: &str) -> Result<String, String> {
    let resp = client
        .post(format!("{api_url}/api/oauth/register"))
        // The server binds snake_case (ClientRegistrationRequest [JsonPropertyName]); the widget
        // only "looks" camelCase because its serializer applies SnakeCaseLower. Send snake_case.
        .json(&serde_json::json!({
            "client_name": CLIENT_NAME,
            "software_id": SOFTWARE_ID,
            "client_uri": CLIENT_URI,
            "redirect_uris": [REDIRECT_URI],
            "scope": DEFAULT_SCOPES,
        }))
        .send()
        .await
        .map_err(|e| format!("Could not reach {api_url}: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("Client registration failed ({}).", oauth_error(resp).await));
    }

    let body: RegisterResponse = resp
        .json()
        .await
        .map_err(|e| format!("Unexpected registration response: {e}"))?;
    Ok(body.client_id)
}

/// Best-effort extraction of an OAuth `error` code from a non-2xx response body.
async fn oauth_error_code(resp: reqwest::Response) -> String {
    match resp.json::<OAuthError>().await {
        Ok(e) => e.error,
        Err(_) => "unknown_error".to_string(),
    }
}

/// Human-readable error string (code + description when present) for messages.
async fn oauth_error(resp: reqwest::Response) -> String {
    let status = resp.status().as_u16();
    match resp.json::<OAuthError>().await {
        Ok(e) => match e.error_description {
            Some(d) => format!("{}: {d}", e.error),
            None => e.error,
        },
        Err(_) => format!("HTTP {status}"),
    }
}

// --- Windows Credential Manager (via keyring) ------------------------------
fn keyring_entry() -> Result<keyring::Entry, String> {
    keyring::Entry::new(CRED_TARGET, CRED_USER)
        .map_err(|e| format!("Could not open the credential store: {e}"))
}

fn load() -> Option<StoredCreds> {
    let secret = keyring_entry().ok()?.get_password().ok()?;
    serde_json::from_str(&secret).ok()
}

fn store(creds: &StoredCreds) -> Result<(), String> {
    let secret = serde_json::to_string(creds).map_err(|e| format!("Could not serialize credentials: {e}"))?;
    keyring_entry()?
        .set_password(&secret)
        .map_err(|e| format!("Could not save credentials: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stored_creds_round_trip() {
        let c = StoredCreds {
            api_url: "https://t.nocturne.run".into(),
            client_id: "abc".into(),
            access_token: "at".into(),
            refresh_token: "rt".into(),
            expires_at_unix: 1_750_000_000,
            scopes: vec!["glucose.read".into(), "therapy.read".into()],
        };
        let json = serde_json::to_string(&c).unwrap();
        let back: StoredCreds = serde_json::from_str(&json).unwrap();
        assert_eq!(c, back);
    }

    #[test]
    fn device_auth_response_parses_snake_case() {
        let json = r#"{
            "device_code":"DC","user_code":"WXYZ-1234",
            "verification_uri":"https://t.nocturne.run/device",
            "verification_uri_complete":"https://t.nocturne.run/device?code=WXYZ-1234",
            "expires_in":900,"interval":5,"extra":"ignored"
        }"#;
        let d: DeviceAuthResponse = serde_json::from_str(json).unwrap();
        assert_eq!(d.user_code, "WXYZ-1234");
        assert_eq!(d.interval, Some(5));
    }

    #[test]
    fn token_response_tolerates_missing_refresh_and_scope() {
        let json = r#"{"access_token":"AT","token_type":"Bearer","expires_in":3600}"#;
        let t: TokenResponse = serde_json::from_str(json).unwrap();
        assert_eq!(t.access_token, "AT");
        assert!(t.refresh_token.is_none());
    }
}
