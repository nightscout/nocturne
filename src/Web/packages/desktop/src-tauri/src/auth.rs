#![allow(dead_code)]

//! Durable OAuth auth for the companion's glucose poller.
//!
//! Ports the Windows 11 widget's `OAuthService` pattern (src/Widgets/.../OAuthService.cs):
//! RFC 7591 dynamic client registration, RFC 8628 device-authorization grant, refresh-token
//! rotation, and credential persistence to the Windows Credential Manager. Read-scoped, plus
//! `device.notify` (toast) and `device.actuate` (tray_flash) so the companion can register as a
//! notify- and tray-flash-capable actuation target.
//!
//! Separate from the CareLink link-code JWT in `main.rs`, which is a one-shot connect credential;
//! this module owns the long-lived credential the poll loop runs on.

use serde::{Deserialize, Serialize};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

const DEFAULT_SCOPES: &str = "glucose.read therapy.read devices.read device.notify device.actuate";
const CLIENT_NAME: &str = "Nocturne Companion";
const SOFTWARE_ID: &str = "nocturne-companion";
const CLIENT_URI: &str = "https://github.com/nightscout/nocturne";
// The device flow never redirects, but RFC 7591 registration requires a redirect URI.
const REDIRECT_URI: &str = "com.nocturne.companion://oauth/callback";

const CRED_TARGET: &str = "Nocturne.Companion.OAuth";
const CRED_USER: &str = "default";

// Refresh this many seconds before the access token expires.
const REFRESH_SKEW_SECS: i64 = 60;

// Serializes refreshes so concurrent callers (the poll loop and the realtime hub) can't fire two
// refresh_token grants at once and replay a rotating (single-use) refresh token, which would
// invalidate the newer one and force a re-link.
static REFRESH_LOCK: std::sync::OnceLock<tokio::sync::Mutex<()>> = std::sync::OnceLock::new();

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

/// Why a token could not be produced. `NotLinked` is definitive — there is no usable credential
/// (never linked, or the refresh grant was rejected) and only a relink fixes it. `Transient` should
/// be retried with the same credential.
#[derive(Debug)]
pub enum TokenError {
    NotLinked(String),
    Transient(String),
}

impl std::fmt::Display for TokenError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            TokenError::NotLinked(m) | TokenError::Transient(m) => f.write_str(m),
        }
    }
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
        .map_err(|e| format!("Could not reach {api_url}: {}", crate::error_chain(&e)))?;

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
            .map_err(|e| format!("Could not reach the server: {}", crate::error_chain(&e)))?;

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
/// within `REFRESH_SKEW_SECS` of expiry. `TokenError::NotLinked` means there is no usable
/// credential (never linked, or the refresh grant was rejected — re-link needed);
/// `TokenError::Transient` means the refresh should be retried later.
pub async fn get_valid_token(client: &reqwest::Client) -> Result<(String, String), TokenError> {
    let creds = load().ok_or_else(|| TokenError::NotLinked("Not linked to a Nocturne server yet.".to_string()))?;
    if now_unix() < creds.expires_at_unix - REFRESH_SKEW_SECS {
        return Ok((creds.api_url, creds.access_token));
    }

    // A refresh is due. Serialize it: another caller may already be refreshing, so take the lock and
    // re-check before spending the single-use refresh token.
    let lock = REFRESH_LOCK.get_or_init(|| tokio::sync::Mutex::new(()));
    let _guard = lock.lock().await;

    let creds = load().ok_or_else(|| TokenError::NotLinked("Not linked to a Nocturne server yet.".to_string()))?;
    if now_unix() < creds.expires_at_unix - REFRESH_SKEW_SECS {
        return Ok((creds.api_url, creds.access_token));
    }
    if creds.refresh_token.is_empty() {
        return Err(TokenError::NotLinked(
            "Session expired and there is no refresh token; please link again.".to_string(),
        ));
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
        .map_err(|e| {
            TokenError::Transient(format!("Could not reach {}: {}", creds.api_url, crate::error_chain(&e)))
        })?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(match resp.json::<OAuthError>().await {
            // `invalid_grant` means the refresh token itself was rejected (revoked/expired): the
            // stored credential is dead. Any other error could be transient.
            Ok(e) if e.error == "invalid_grant" => TokenError::NotLinked(format!(
                "Token refresh was rejected ({}); please link again.",
                describe_oauth(&e)
            )),
            Ok(e) => TokenError::Transient(format!("Token refresh failed ({}).", describe_oauth(&e))),
            Err(_) => TokenError::Transient(format!("Token refresh failed (HTTP {status}).")),
        });
    }

    let token: TokenResponse = resp
        .json()
        .await
        .map_err(|e| TokenError::Transient(format!("Unexpected refresh response: {e}")))?;

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
    store(&refreshed).map_err(TokenError::Transient)?;
    Ok((refreshed.api_url, token.access_token))
}

pub fn is_linked() -> bool {
    load().is_some()
}

/// The scopes granted to the stored credential, if linked. Empty when the server did not echo a
/// `scope` on the token response (callers should treat that as unknown, not missing).
pub fn granted_scopes() -> Option<Vec<String>> {
    load().map(|c| c.scopes)
}

/// Extracts the `sub` (subject id) claim from a JWT access token without verifying its signature —
/// the token is our own, already trusted for the HTTP calls it authorizes. Splits on '.', base64url-
/// decodes the payload segment, and reads `sub`. Returns `None` if the token isn't a well-formed JWT
/// or has no string `sub`. Used to filter fan-out SignalR events (e.g. `device_notification`) to this
/// device's user in a multi-user tenant.
pub fn subject_from_token(token: &str) -> Option<String> {
    let payload_b64 = token.split('.').nth(1)?;
    let payload = base64url_decode(payload_b64)?;
    let json: serde_json::Value = serde_json::from_slice(&payload).ok()?;
    json.get("sub").and_then(|s| s.as_str()).map(str::to_string)
}

/// Decodes an unpadded base64url segment (JWT alphabet: `-`/`_`, no `=` padding). Returns `None` on
/// any invalid character or a truncated (length ≡ 1 mod 4) input.
fn base64url_decode(input: &str) -> Option<Vec<u8>> {
    fn val(c: u8) -> Option<u8> {
        match c {
            b'A'..=b'Z' => Some(c - b'A'),
            b'a'..=b'z' => Some(c - b'a' + 26),
            b'0'..=b'9' => Some(c - b'0' + 52),
            b'-' => Some(62),
            b'_' => Some(63),
            _ => None,
        }
    }

    let bytes = input.as_bytes();
    if bytes.len() % 4 == 1 {
        return None;
    }

    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for chunk in bytes.chunks(4) {
        let mut buf = [0u8; 4];
        let mut n = 0;
        for (i, &b) in chunk.iter().enumerate() {
            buf[i] = val(b)?;
            n += 1;
        }
        // n symbols -> n*6 bits -> (n*6)/8 whole bytes.
        let combined = (u32::from(buf[0]) << 18)
            | (u32::from(buf[1]) << 12)
            | (u32::from(buf[2]) << 6)
            | u32::from(buf[3]);
        if n >= 2 {
            out.push((combined >> 16) as u8);
        }
        if n >= 3 {
            out.push((combined >> 8) as u8);
        }
        if n >= 4 {
            out.push(combined as u8);
        }
    }
    Some(out)
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
        .map_err(|e| format!("Could not reach {api_url}: {}", crate::error_chain(&e)))?;

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
        Ok(e) => describe_oauth(&e),
        Err(_) => format!("HTTP {status}"),
    }
}

/// `error: error_description` when a description is present, else just the code.
fn describe_oauth(e: &OAuthError) -> String {
    match &e.error_description {
        Some(d) => format!("{}: {d}", e.error),
        None => e.error.clone(),
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

    #[test]
    fn subject_from_token_reads_sub_claim() {
        // header . payload({"sub":"user-42","name":"Rhys"}) . signature — signature not verified.
        let jwt = "eyJhbGciOiJIUzI1NiJ9\
                   .eyJzdWIiOiJ1c2VyLTQyIiwibmFtZSI6IlJoeXMifQ\
                   .c2lnbmF0dXJl";
        assert_eq!(subject_from_token(jwt).as_deref(), Some("user-42"));
    }

    #[test]
    fn subject_from_token_rejects_non_jwt() {
        assert!(subject_from_token("not-a-jwt").is_none());
        // Two segments but the payload has no `sub`.
        let no_sub = "eyJhbGciOiJIUzI1NiJ9.eyJuYW1lIjoiUmh5cyJ9";
        assert!(subject_from_token(no_sub).is_none());
    }

    #[test]
    fn base64url_decode_round_trips_and_rejects_bad_input() {
        // "Man" -> "TWFu" (no padding needed), 3 bytes.
        assert_eq!(base64url_decode("TWFu").unwrap(), b"Man");
        // "M" -> "TQ" (2 symbols -> 1 byte).
        assert_eq!(base64url_decode("TQ").unwrap(), b"M");
        // Length ≡ 1 mod 4 is impossible in valid base64.
        assert!(base64url_decode("TWF").is_some()); // 3 symbols -> 2 bytes, valid
        assert!(base64url_decode("A").is_none()); // 1 symbol, truncated
        assert!(base64url_decode("**").is_none()); // invalid chars
    }
}
