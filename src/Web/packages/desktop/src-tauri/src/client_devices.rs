//! Client-device registration and the active-intents reconcile snapshot.
//!
//! Wraps `POST /api/v4/client-devices` (idempotent upsert keyed on install id) and
//! `GET /api/v4/client-devices/{id}/active-intents` (the reconcile source of truth). The wire shape
//! is camelCase JSON matching the server's `RegisterDeviceRequest` / `ClientDeviceDto` /
//! `DeviceActionIntent`. mg/dL glucose stays canonical here; display conversion is the toast's job.

use serde::{Deserialize, Serialize};

/// Error from a client-devices API call. `status` carries the HTTP status when a response was
/// received, so callers branch on it (e.g. 409 on register → regenerate the install id) instead of
/// parsing message strings.
#[derive(Debug)]
pub struct ApiError {
    pub status: Option<u16>,
    pub message: String,
}

impl ApiError {
    fn network(message: String) -> Self {
        Self { status: None, message }
    }

    fn http(status: u16, message: String) -> Self {
        Self { status: Some(status), message }
    }
}

impl std::fmt::Display for ApiError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.message)
    }
}

/// This app's device kind, per `DeviceKinds.Companion` server-side.
pub const DEVICE_KIND: &str = "companion";
/// Show a native toast (`DeviceCapabilities.Notify`; requires the `device.notify` scope).
pub const NOTIFY_CAPABILITY: &str = "notify";
/// Flash the system tray icon (`DeviceCapabilities.TrayFlash`; requires the `device.actuate` scope).
pub const TRAY_FLASH_CAPABILITY: &str = "tray_flash";

/// Capabilities this device can actuate. The server narrows each intent's `capabilities` to the
/// intersection of what the rule requested and what the device advertises, so advertising a
/// capability is what enables actuating it — which is why registration sends only the subset the
/// user has left enabled (`device_capabilities`), not this whole set.
pub const ADVERTISED_CAPABILITIES: [&str; 2] = [NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY];

/// Registration request body (camelCase to match `RegisterDeviceRequest`).
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct RegisterRequest<'a> {
    install_id: &'a str,
    kind: &'a str,
    label: &'a str,
    capabilities: &'a [&'a str],
}

/// Subset of `ClientDeviceDto` we need back: the server-assigned id keys the snapshot endpoint.
#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct RegisterResponse {
    id: String,
}

/// A device actuation intent, matching the server's `DeviceActionIntent`. `excursionId` is the
/// stable dedup/correlation key shared by the live SignalR event and this snapshot. `severity` is a
/// lowercase string (`critical`/`warning`/`info`); `glucoseValue`/`trend` are live-only and null in
/// the snapshot. Tolerant of fields we don't use.
#[derive(Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceActionIntent {
    #[serde(default = "default_intent")]
    pub intent: String,
    pub excursion_id: String,
    #[serde(default)]
    pub rule_name: String,
    #[serde(default)]
    pub severity: String,
    /// Capabilities the server narrowed to this device (rule request ∩ advertised). Drives which
    /// actuations fire for the intent.
    #[serde(default)]
    pub capabilities: Vec<String>,
    #[serde(default)]
    pub acknowledged: bool,
    #[serde(default)]
    pub glucose_value: Option<f64>,
    #[serde(default)]
    pub trend: Option<String>,
}

fn default_intent() -> String {
    "opened".to_string()
}

impl DeviceActionIntent {
    /// Whether this intent should currently drive actuation: it is in the `opened` phase and not
    /// acknowledged. `resolved`/`acknowledged` phases and the `acknowledged` flag both withdraw.
    pub fn is_active(&self) -> bool {
        self.intent == "opened" && !self.acknowledged
    }

    /// Whether this intent requests a native toast for this device.
    pub fn wants_notify(&self) -> bool {
        self.capabilities.iter().any(|c| c == NOTIFY_CAPABILITY)
    }

    /// Whether this intent requests a tray-icon flash for this device.
    pub fn wants_tray_flash(&self) -> bool {
        self.capabilities.iter().any(|c| c == TRAY_FLASH_CAPABILITY)
    }
}

/// Registers (or refreshes) this install with the server and returns its server-assigned device id.
/// Idempotent — called on every startup. The label is the machine name when resolvable, else a
/// generic fallback. `capabilities` replaces whatever the server has stored for this install, so a
/// re-registration with a narrower set is how a capability is withdrawn.
pub async fn register(
    client: &reqwest::Client,
    server: &str,
    token: &str,
    install_id: &str,
    label: &str,
    capabilities: &[&str],
) -> Result<String, ApiError> {
    let server = server.trim_end_matches('/');
    let body = RegisterRequest {
        install_id,
        kind: DEVICE_KIND,
        label,
        capabilities,
    };

    let resp = client
        .post(format!("{server}/api/v4/client-devices"))
        .bearer_auth(token)
        .json(&body)
        .send()
        .await
        .map_err(|e| ApiError::network(format!("Could not reach {server}: {e}")))?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(ApiError::http(status, format!("device registration returned HTTP {status}")));
    }

    let device: RegisterResponse = resp
        .json()
        .await
        .map_err(|e| ApiError::network(format!("Unexpected registration response: {e}")))?;
    Ok(device.id)
}

/// Fetches the active-intents reconcile snapshot for `device_id`. The server returns only intents
/// still active for this device; anything resolved/acked is simply absent.
pub async fn active_intents(
    client: &reqwest::Client,
    server: &str,
    token: &str,
    device_id: &str,
) -> Result<Vec<DeviceActionIntent>, ApiError> {
    let server = server.trim_end_matches('/');
    let resp = client
        .get(format!("{server}/api/v4/client-devices/{device_id}/active-intents"))
        .bearer_auth(token)
        .send()
        .await
        .map_err(|e| ApiError::network(format!("Could not reach {server}: {e}")))?;

    if !resp.status().is_success() {
        let status = resp.status().as_u16();
        return Err(ApiError::http(status, format!("active-intents returned HTTP {status}")));
    }

    resp.json::<Vec<DeviceActionIntent>>()
        .await
        .map_err(|e| ApiError::network(format!("Unexpected active-intents response: {e}")))
}

/// Acknowledges an excursion (`POST /api/v4/alerts/excursions/{id}/acknowledge`). Best-effort: wired
/// to the toast's Acknowledge action. A 404 (excursion already gone) or any non-2xx is returned as
/// an error for the caller to log, never panics.
pub async fn acknowledge(
    client: &reqwest::Client,
    server: &str,
    token: &str,
    excursion_id: &str,
    acknowledged_by: &str,
) -> Result<(), String> {
    let server = server.trim_end_matches('/');
    let resp = client
        .post(format!("{server}/api/v4/alerts/excursions/{excursion_id}/acknowledge"))
        .bearer_auth(token)
        .json(&serde_json::json!({ "acknowledgedBy": acknowledged_by }))
        .send()
        .await
        .map_err(|e| format!("Could not reach {server}: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("acknowledge returned HTTP {}", resp.status().as_u16()));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn intent_parses_snapshot_camel_case_and_tolerates_missing_live_fields() {
        let json = r#"[{
            "intent":"opened","excursionId":"11111111-1111-1111-1111-111111111111",
            "ruleName":"Low glucose","severity":"critical","targetKind":"companion",
            "capabilities":["notify","tray_flash"],"acknowledged":false,
            "startedAt":"2026-06-30T10:00:00Z"
        }]"#;
        let intents: Vec<DeviceActionIntent> = serde_json::from_str(json).unwrap();
        assert_eq!(intents.len(), 1);
        let i = &intents[0];
        assert_eq!(i.excursion_id, "11111111-1111-1111-1111-111111111111");
        assert_eq!(i.rule_name, "Low glucose");
        assert_eq!(i.severity, "critical");
        assert!(i.glucose_value.is_none());
        assert!(i.is_active());
        assert!(i.wants_notify());
        assert!(i.wants_tray_flash());
    }

    #[test]
    fn capability_helpers_reflect_narrowed_set() {
        let json = r#"{
            "intent":"opened","excursionId":"33333333-3333-3333-3333-333333333333",
            "severity":"warning","capabilities":["notify"],"acknowledged":false
        }"#;
        let i: DeviceActionIntent = serde_json::from_str(json).unwrap();
        assert!(i.wants_notify());
        assert!(!i.wants_tray_flash());
    }

    #[test]
    fn intent_parses_live_event_with_glucose_and_trend() {
        let json = r#"{
            "intent":"opened","excursionId":"22222222-2222-2222-2222-222222222222",
            "ruleName":"High","severity":"warning","acknowledged":false,
            "glucoseValue":250.0,"trend":"SingleUp"
        }"#;
        let i: DeviceActionIntent = serde_json::from_str(json).unwrap();
        assert_eq!(i.glucose_value, Some(250.0));
        assert_eq!(i.trend.as_deref(), Some("SingleUp"));
        assert!(i.is_active());
    }

    #[test]
    fn acknowledged_or_resolved_intent_is_not_active() {
        let acked = DeviceActionIntent {
            intent: "opened".into(),
            excursion_id: "x".into(),
            rule_name: String::new(),
            severity: "info".into(),
            capabilities: vec![NOTIFY_CAPABILITY.into()],
            acknowledged: true,
            glucose_value: None,
            trend: None,
        };
        assert!(!acked.is_active());

        let resolved = DeviceActionIntent { intent: "resolved".into(), acknowledged: false, ..acked.clone() };
        assert!(!resolved.is_active());
    }
}
