//! Native Windows toasts.
//!
//! Uses the WinRT toast API (`tauri-winrt-notification`) rather than the generic tauri notification
//! plugin. Two distinct paths:
//! - `show` — device-action *alerts*: actionable (an Acknowledge or Mute for me button, see
//!   [`AckAction`]) and, for critical severity,
//!   persistent (`Scenario::Alarm` pre-expands, stays until dismissed, loops alarm audio).
//! - `show_notification` — ambient in-app *notification mirrors* (`device_notification`): a plain
//!   banner with a normal duration, no alarm scenario and no action buttons.
//!
//! The action button's activation runs on a WinRT event-handler thread, so the closure owns the
//! server URL and excursion id and spawns the ack HTTP call on the provided Tokio runtime handle
//! (`on_activated` requires a `'static` closure). The bearer token is deliberately NOT captured:
//! critical toasts persist until dismissed, which can be long past the reconcile-time token's expiry,
//! so a fresh token is resolved when the button is clicked.

use crate::client_devices::DeviceActionIntent;
use crate::glucose_poll::MGDL_PER_MMOL;
use crate::signalr::InAppNotification;
use tauri_winrt_notification::{Duration, Scenario, Toast};

/// App id the toast is attributed to. Reusing the PowerShell app id avoids needing a registered
/// AppUserModelID/shortcut for toasts to appear; the dev companion isn't Start-menu-registered.
const APP_ID: &str = Toast::POWERSHELL_APP_ID;

/// Button action argument recognised by the activation handler.
const ACK_ACTION: &str = "acknowledge";

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

/// What pressing the toast's button does, as the server reported it for this user on the snapshot
/// (`acknowledgesForEveryone`). A user without `alerts.readwrite` only mutes the alert for
/// themselves; everyone else keeps being alerted.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum AckAction {
    Acknowledge,
    Mute,
}

impl AckAction {
    /// An intent without the flag came from a server that acknowledges every press for everyone.
    fn for_intent(intent: &DeviceActionIntent) -> Self {
        match intent.acknowledges_for_everyone {
            Some(false) => AckAction::Mute,
            _ => AckAction::Acknowledge,
        }
    }

    fn button_label(self) -> &'static str {
        match self {
            AckAction::Acknowledge => "Acknowledge",
            AckAction::Mute => "Mute for me",
        }
    }

    /// `(title, body)` of the notification shown when the request fails.
    fn failure_lines(self) -> (&'static str, &'static str) {
        match self {
            AckAction::Acknowledge => (
                "Alert not acknowledged",
                "The acknowledge request failed. Acknowledge it in Nocturne.",
            ),
            AckAction::Mute => (
                "Alert not muted",
                "The mute request failed. Mute it in Nocturne.",
            ),
        }
    }

    /// `(title, body)` of a notification for a press the server handled differently from the
    /// button's label: the user's permissions changed after the toast was shown. `None` when the
    /// outcome matches the label, or the alert had already closed.
    fn outcome_mismatch_lines(self, outcome: &str) -> Option<(&'static str, &'static str)> {
        match (self, outcome) {
            (AckAction::Acknowledge, "muted") => Some((
                "Alert muted for you only",
                "You can no longer acknowledge alerts for everyone, so others are still being alerted.",
            )),
            (AckAction::Mute, "acknowledged") => Some((
                "Alert acknowledged for everyone",
                "This alert has stopped for everyone, not just you.",
            )),
            _ => None,
        }
    }
}

/// Shows a toast for `intent`. Critical severity gets the persistent alarm scenario; others a
/// long-duration banner. When `ack` is provided, a button labelled by [`AckAction`] calls the ack
/// endpoint on click. Best-effort: a WinRT failure is returned as an error string and never panics.
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
        let ack_action = AckAction::for_intent(intent);
        toast = toast.add_button(ack_action.button_label(), ACK_ACTION);
        toast = toast.on_activated(move |action| {
            if action.as_deref() == Some(ACK_ACTION) {
                let ack = ack.clone();
                let runtime = ack.runtime.clone();
                runtime.spawn(async move { acknowledge_excursion(&ack, ack_action).await });
            }
            Ok(())
        });
    }

    toast
        .show()
        .map_err(|e| format!("could not show toast: {e}"))
}

/// Posts the acknowledge for `ack`, resolving a fresh token at click time. The fresh token is only
/// sent to the server it belongs to: if the user relinked to a different server while the toast
/// persisted, the ack is dropped (the new server's bearer must not go to the old server's URL, and
/// the old server's excursion can't be acknowledged with the new credential anyway) and the failure
/// notification shown. On failure the error is logged and a plain notification toast reports that
/// the press did not go through; on success a notification appears only when the server's outcome
/// differs from what the button said.
async fn acknowledge_excursion(ack: &AckContext, action: AckAction) {
    let result = async {
        let client = crate::http::client()?;
        let (server, token) = crate::auth::get_valid_token(&client)
            .await
            .map_err(|e| e.to_string())?;
        check_ack_server(&server, &ack.server)?;
        crate::client_devices::acknowledge(&client, &server, &token, &ack.excursion_id, ACK_BY)
            .await
    }
    .await;

    let (id, (title, body)) = match result {
        Ok(response) => match action.outcome_mismatch_lines(&response.outcome) {
            Some(lines) => (format!("ack-outcome-{}", ack.excursion_id), lines),
            None => return,
        },
        Err(e) => {
            eprintln!("alert ack: {e}");
            (
                format!("ack-failed-{}", ack.excursion_id),
                action.failure_lines(),
            )
        }
    };
    let _ = show_notification(&InAppNotification {
        id,
        title: title.to_string(),
        subtitle: Some(body.to_string()),
    });
}

/// Guards against cross-server token disclosure: `current` is the server the fresh token belongs
/// to, `toast` the server the excursion was toasted from. A mismatch means the user relinked to a
/// different server while the toast persisted — the ack must not be sent. Compared ignoring a
/// trailing slash; both come from the stored credential's api_url, so no deeper normalization is
/// needed.
fn check_ack_server(current: &str, toast: &str) -> Result<(), String> {
    if current.trim_end_matches('/') == toast.trim_end_matches('/') {
        return Ok(());
    }
    Err(format!(
        "linked server changed ({toast} -> {current}); dropping acknowledge for an excursion on the previous server"
    ))
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
    let name = if intent.rule_name.is_empty() {
        "Glucose alert"
    } else {
        &intent.rule_name
    };
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
        Some(mgdl) => format!("{severity} \u{00b7} {:.1} mmol/L", mgdl / MGDL_PER_MMOL),
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
            acknowledges_for_everyone: None,
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

    #[test]
    fn ack_allowed_when_token_server_matches_toast_server() {
        assert!(check_ack_server("https://t.nocturne.run", "https://t.nocturne.run").is_ok());
        // Trailing-slash difference is not a relink.
        assert!(check_ack_server("https://t.nocturne.run/", "https://t.nocturne.run").is_ok());
    }

    #[test]
    fn ack_blocked_when_relinked_to_a_different_server() {
        let err = check_ack_server("https://new.nocturne.run", "https://old.nocturne.run")
            .expect_err("the new server's bearer must not be posted to the old server");
        assert!(err.contains("old.nocturne.run"));
        assert!(err.contains("new.nocturne.run"));
    }

    #[test]
    fn member_without_alerts_readwrite_gets_a_mute_button_and_mute_failure_copy() {
        let mut i = intent("critical", None);
        i.acknowledges_for_everyone = Some(false);
        let action = AckAction::for_intent(&i);
        assert_eq!(action.button_label(), "Mute for me");
        assert_eq!(action.failure_lines().0, "Alert not muted");
    }

    #[test]
    fn member_with_alerts_readwrite_or_an_unflagged_intent_gets_acknowledge() {
        let mut i = intent("warning", None);
        assert_eq!(AckAction::for_intent(&i).button_label(), "Acknowledge");
        i.acknowledges_for_everyone = Some(true);
        let action = AckAction::for_intent(&i);
        assert_eq!(action.button_label(), "Acknowledge");
        assert_eq!(action.failure_lines().0, "Alert not acknowledged");
    }

    #[test]
    fn outcome_notice_only_when_the_server_did_something_else() {
        assert_eq!(AckAction::Mute.outcome_mismatch_lines("muted"), None);
        assert_eq!(
            AckAction::Acknowledge.outcome_mismatch_lines("acknowledged"),
            None
        );
        assert_eq!(AckAction::Mute.outcome_mismatch_lines("closed"), None);
        assert_eq!(
            AckAction::Acknowledge
                .outcome_mismatch_lines("muted")
                .map(|l| l.0),
            Some("Alert muted for you only")
        );
        assert_eq!(
            AckAction::Mute
                .outcome_mismatch_lines("acknowledged")
                .map(|l| l.0),
            Some("Alert acknowledged for everyone")
        );
    }
}
