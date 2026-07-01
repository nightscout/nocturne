#![allow(dead_code)]

//! Fetches the Nocturne V4 "widget-friendly" summary and writes it to the local file the taskbar
//! mod / Win11 widget read. The file IS the raw `V4SummaryResponse` from `GET /api/v4/summary`
//! (current glucose, history, IOB, COB, predictions, alarm) — no bespoke DTO; consumers parse the
//! V4 shape directly. We additionally parse out just `current` so the tray icon can render the
//! latest value. Values stay mg/dL (canonical V4 `sgv`); display-unit conversion is the
//! consumer's job.

use serde::Deserialize;

// History window to request (sparkline span). includePredictions=true is harmless when the tenant
// has no prediction engine — the field just comes back empty.
const SUMMARY_HOURS: u32 = 3;

/// The latest reading, parsed from the summary for the tray icon and the readout. `sgv`/`delta`
/// are mg/dL; display-unit conversion is the consumer's job.
#[derive(Clone, Debug, serde::Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CurrentBg {
    pub sgv_mgdl: f64,
    pub delta_mgdl: Option<f64>,
    pub direction: Option<String>,
    pub mills: i64,
}

// Tolerant: we only need `current` here; everything else passes through to the file untouched.
#[derive(Deserialize)]
struct SummaryHead {
    current: Option<CurrentReading>,
}
#[derive(Deserialize)]
struct CurrentReading {
    sgv: Option<f64>,
    delta: Option<f64>,
    direction: Option<String>,
    mills: Option<i64>,
}

impl CurrentReading {
    /// Maps the parsed `current` to a `CurrentBg`, or `None` when there is no `sgv`.
    fn into_current_bg(self) -> Option<CurrentBg> {
        self.sgv.map(|sgv| CurrentBg {
            sgv_mgdl: sgv,
            delta_mgdl: self.delta,
            direction: self.direction,
            mills: self.mills.unwrap_or(0),
        })
    }
}

/// Reads the last-written glucose file and parses just `current` (None if the file is missing,
/// unreadable, or carries no current reading). Lets the UI show the latest value on launch,
/// before the first poll of the session completes.
pub fn read_current_from_file() -> Option<CurrentBg> {
    let bytes = std::fs::read(crate::glucose_file::glucose_file_path()).ok()?;
    serde_json::from_slice::<SummaryHead>(&bytes)
        .ok()?
        .current?
        .into_current_bg()
}

/// Fetches `GET /api/v4/summary` and writes the raw response to the local file (atomic). Returns
/// the parsed current reading for the tray icon (None if the summary carries no `current`).
pub async fn poll_once(
    client: &reqwest::Client,
    server: &str,
    token: &str,
) -> Result<Option<CurrentBg>, String> {
    let server = server.trim_end_matches('/');
    let hours = SUMMARY_HOURS.to_string();
    let resp = client
        .get(format!("{server}/api/v4/summary"))
        .query(&[("hours", hours.as_str()), ("includePredictions", "true")])
        .bearer_auth(token)
        .send()
        .await
        .map_err(|e| format!("Could not reach {server}: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("summary returned HTTP {}", resp.status().as_u16()));
    }

    let body = resp
        .bytes()
        .await
        .map_err(|e| format!("Could not read summary response: {e}"))?;
    if body.is_empty() {
        return Err("summary response was empty".to_string());
    }
    crate::glucose_file::write_glucose_file(&body).map_err(|e| e.to_string())?;

    // Best-effort parse of just `current` for the tray icon; a parse miss never fails the write.
    let current = serde_json::from_slice::<SummaryHead>(&body)
        .ok()
        .and_then(|h| h.current)
        .and_then(CurrentReading::into_current_bg);
    Ok(current)
}
