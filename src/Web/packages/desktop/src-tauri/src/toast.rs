//! Native Windows toasts.
//!
//! Uses the WinRT toast API (`tauri-winrt-notification`) rather than the generic tauri notification
//! plugin. Two distinct paths:
//! - `show` — device-action *alerts*: actionable (an Acknowledge button) and, for critical severity,
//!   persistent (`Scenario::Alarm` pre-expands, stays until dismissed, loops alarm audio).
//! - `show_notification` — ambient in-app *notification mirrors* (`device_notification`): a plain
//!   banner with a normal duration, no alarm scenario and no action buttons.
//!
//! The Acknowledge button's activation runs on a WinRT event-handler thread, so the closure owns the
//! server URL and excursion id and spawns the ack HTTP call on the provided Tokio runtime handle
//! (`on_activated` requires a `'static` closure). The bearer token is deliberately NOT captured:
//! critical toasts persist until dismissed, which can be long past the reconcile-time token's expiry,
//! so a fresh token is resolved when the button is clicked.

use crate::client_devices::DeviceActionIntent;
use crate::signalr::InAppNotification;
use tauri_winrt_notification::{Duration, Scenario, Toast};

/// App id the toast is attributed to. Reusing the PowerShell app id avoids needing a registered
/// AppUserModelID/shortcut for toasts to appear; the dev companion isn't Start-menu-registered.
const APP_ID: &str = Toast::POWERSHELL_APP_ID;

/// Button action argument recognised by the activation handler.
const ACK_ACTION: &str = "acknowledge";
const MMOL_PER_MGDL: f64 = 18.0182;

/// Who an ack from this toast is attributed to server-side (`acknowledgedBy`).
const ACK_BY: &str = "desktop-companion";

/// What identifies the ack, captured into the toast's activation closure. The token is resolved
/// fresh at click time (see module docs), so only the target server and excursion are carried.
#[derive(Clone)]
pub struct AckContext {
    pub server: String,
    pub excursion_id: String,
    pub runtime: tokio::runtime::Handle,
}

/// Shows a toast for `intent`. Critical severity gets the persistent alarm scenario; others a
/// long-duration banner. When `ack` is provided, an Acknowledge button calls the ack endpoint on
/// click. Best-effort: a WinRT failure is returned as an error string and never panics.
pub fn show(intent: &DeviceActionIntent, ack: Option<AckContext>) -> Result<(), String> {
    let critical = intent.severity == "critical";

    let mut toast = Toast::new(APP_ID)
        .title(&title_line(intent))
        .text1(&body_line(intent));

    toast = if critical {
        toast.scenario(Scenario::Alarm)
    } else {
        toast.duration(Duration::Long)
    };

    if let Some(ack) = ack {
        toast = toast.add_button("Acknowledge", ACK_ACTION);
        toast = toast.on_activated(move |action| {
            if action.as_deref() == Some(ACK_ACTION) {
                let ack = ack.clone();
                let runtime = ack.runtime.clone();
                runtime.spawn(async move { acknowledge_excursion(&ack).await });
            }
            Ok(())
        });
    }

    toast.show().map_err(|e| format!("could not show toast: {e}"))
}

/// Posts the acknowledge for `ack`, resolving a fresh token at click time. On failure the error is
/// logged and a plain notification toast reports that the acknowledge did not go through.
async fn acknowledge_excursion(ack: &AckContext) {
    let result = async {
        let client = crate::http::client()?;
        let (_, token) = crate::auth::get_valid_token(&client).await.map_err(|e| e.to_string())?;
        crate::client_devices::acknowledge(&client, &ack.server, &token, &ack.excursion_id, ACK_BY)
            .await
    }
    .await;

    if let Err(e) = result {
        eprintln!("alert ack: {e}");
        let _ = show_notification(&InAppNotification {
            id: format!("ack-failed-{}", ack.excursion_id),
            title: "Alert not acknowledged".to_string(),
            subtitle: Some("The acknowledge request failed. Acknowledge it in Nocturne.".to_string()),
        });
    }
}

/// Shows an ambient notification toast mirroring an in-app notification. Unlike `show`, this is a
/// plain banner (`Duration::Short`, no alarm scenario, no action buttons) — the server has already
/// gated which notifications are worth surfacing. Title falls back to a generic label if absent, and
/// the body uses the subtitle (or the title again when there is no subtitle).
pub fn show_notification(notification: &InAppNotification) -> Result<(), String> {
    let (title, body) = notification_lines(notification);
    Toast::new(APP_ID)
        .title(&title)
        .text1(&body)
        .duration(Duration::Short)
        .show()
        .map_err(|e| format!("could not show notification toast: {e}"))
}

/// Derives the `(title, body)` for a notification toast: title falls back to a generic label when
/// blank; body uses a non-blank subtitle, else repeats the title.
fn notification_lines(notification: &InAppNotification) -> (String, String) {
    let title = match notification.title.trim() {
        "" => "Nocturne",
        t => t,
    };
    let body = notification
        .subtitle
        .as_deref()
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .unwrap_or(title);
    (title.to_string(), body.to_string())
}

/// Title line: rule name, prefixed with a severity marker for critical/warning.
fn title_line(intent: &DeviceActionIntent) -> String {
    let name = if intent.rule_name.is_empty() { "Glucose alert" } else { &intent.rule_name };
    match intent.severity.as_str() {
        "critical" => format!("\u{26A0} {name}"),
        "warning" => format!("\u{26A0} {name}"),
        _ => name.to_string(),
    }
}

/// Body line: severity word plus the live glucose value (mmol/L, when present in the live event).
fn body_line(intent: &DeviceActionIntent) -> String {
    let severity = match intent.severity.as_str() {
        "critical" => "Critical",
        "warning" => "Warning",
        "info" => "Info",
        other if !other.is_empty() => other,
        _ => "Alert",
    };
    match intent.glucose_value {
        Some(mgdl) => format!("{severity} \u{00b7} {:.1} mmol/L", mgdl / MMOL_PER_MGDL),
        None => severity.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn intent(severity: &str, glucose: Option<f64>) -> DeviceActionIntent {
        DeviceActionIntent {
            intent: "opened".into(),
            excursion_id: "e".into(),
            rule_name: "Low glucose".into(),
            severity: severity.into(),
            capabilities: vec!["notify".into()],
            acknowledged: false,
            glucose_value: glucose,
            trend: None,
        }
    }

    #[test]
    fn body_includes_mmol_when_glucose_present() {
        let b = body_line(&intent("critical", Some(54.0)));
        assert!(b.starts_with("Critical"));
        assert!(b.contains("3.0 mmol/L"));
    }

    #[test]
    fn body_is_severity_only_without_glucose() {
        assert_eq!(body_line(&intent("warning", None)), "Warning");
    }

    #[test]
    fn title_falls_back_when_rule_name_empty() {
        let mut i = intent("info", None);
        i.rule_name = String::new();
        assert_eq!(title_line(&i), "Glucose alert");
    }

    fn notification(title: &str, subtitle: Option<&str>) -> InAppNotification {
        InAppNotification {
            id: "n1".into(),
            title: title.into(),
            subtitle: subtitle.map(str::to_string),
        }
    }

    #[test]
    fn notification_uses_title_and_subtitle() {
        let (t, b) = notification_lines(&notification("Sync complete", Some("3 devices updated")));
        assert_eq!(t, "Sync complete");
        assert_eq!(b, "3 devices updated");
    }

    #[test]
    fn notification_body_falls_back_to_title_without_subtitle() {
        let (t, b) = notification_lines(&notification("Sync complete", None));
        assert_eq!(t, "Sync complete");
        assert_eq!(b, "Sync complete");
        // A blank subtitle is treated as absent.
        let (_, b2) = notification_lines(&notification("Sync complete", Some("  ")));
        assert_eq!(b2, "Sync complete");
    }

    #[test]
    fn notification_title_falls_back_when_blank() {
        let (t, b) = notification_lines(&notification("", None));
        assert_eq!(t, "Nocturne");
        assert_eq!(b, "Nocturne");
    }
}
