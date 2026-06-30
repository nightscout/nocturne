//! Device-action actuation loop: register, listen for `device_action` over SignalR, and reconcile
//! against the active-intents snapshot to drive Windows toasts.
//!
//! The active-intents snapshot is the source of truth, not the live event. A live `device_action`
//! only triggers an immediate reconcile; the reconcile diffs the server's active set against an
//! in-memory "currently actuating" set keyed on excursion id, so it is idempotent (a live event
//! overlapping the periodic poll cannot double-toast) and self-healing (an alert closed while the
//! companion was offline simply never appears, producing no toast). Acknowledged intents are
//! skipped. Reconcile runs on connect, on every `device_action`, and on a ~30s interval.
//!
//! Transport is the hand-rolled `signalr` client; if a connection attempt fails the loop falls back
//! to pure periodic reconcile until the next reconnect succeeds, so actuation degrades to polling
//! rather than stopping.

use crate::client_devices::{self, DeviceActionIntent};
use crate::toast::{self, AckContext};
use std::collections::HashSet;
use std::time::Duration;
use tokio::sync::mpsc;

/// Periodic reconcile cadence — the safety net under the live SignalR push.
const RECONCILE_SECS: u64 = 30;
/// Max lifetime of one SignalR session before it is recycled to refresh the connection's bearer
/// (the access token is also a query param on the WS handshake, so a long-lived socket would
/// outlive its token). Reconcile keeps running across the recycle; the gap is a reconnect, not an
/// actuation outage.
const SESSION_SECS: u64 = 25 * 60;
/// Backoff before retrying SignalR when a connect fails; periodic reconcile still runs in the gap.
const RECONNECT_SECS: u64 = 30;

/// Drives registration + actuation for as long as the app runs. Idles (retrying) while unlinked,
/// resolving `(server, token)` via the same durable-OAuth path the glucose poller uses.
pub async fn run(app: tauri::AppHandle) {
    let _ = app; // reserved for future tray_flash actuation; toasts don't need the handle.
    let install_id = crate::install_id::get_or_create();
    let label = machine_label();
    let runtime = tokio::runtime::Handle::current();

    let mut actuating: HashSet<String> = HashSet::new();

    loop {
        // Resolve a token; if unlinked or refresh fails, wait and retry (mirrors the glucose poller).
        let client = match http_client() {
            Ok(c) => c,
            Err(e) => {
                eprintln!("alert actuation: {e}");
                tokio::time::sleep(Duration::from_secs(RECONCILE_SECS)).await;
                continue;
            }
        };
        let (server, token) = match crate::auth_token(&client).await {
            Ok(pair) => pair,
            Err(_) => {
                tokio::time::sleep(Duration::from_secs(RECONCILE_SECS)).await;
                continue;
            }
        };

        // Register (idempotent) to get this install's server device id.
        let device_id = match client_devices::register(&client, &server, &token, &install_id, &label).await {
            Ok(id) => id,
            Err(e) => {
                eprintln!("alert actuation: register: {e}");
                tokio::time::sleep(Duration::from_secs(RECONCILE_SECS)).await;
                continue;
            }
        };

        // One session: hold a SignalR connection (best-effort) and reconcile on connect, on each
        // device_action, and on the periodic tick. Returns when the session ages out or the
        // connection drops, so the outer loop re-resolves the token and re-registers. The pair just
        // resolved for registration is reused for this session's connect + first reconcile.
        run_session(&client, (server, token), &device_id, &mut actuating, &runtime).await;
    }
}

/// Runs one actuation session until it should be torn down (connection lost or session age-out).
/// Spawns the SignalR receive task (if it connects) and owns the reconcile timer. `creds` is the
/// `(server, token)` pair the outer loop resolved for registration; it is reused for this session's
/// SignalR connect and first reconcile so a single refresh covers both.
async fn run_session(
    client: &reqwest::Client,
    creds: (String, String),
    device_id: &str,
    actuating: &mut HashSet<String>,
    runtime: &tokio::runtime::Handle,
) {
    // Channel the SignalR task uses to nudge a reconcile on each device_action.
    let (tx, mut rx) = mpsc::channel::<()>(8);

    let (server, token) = creds;

    // Try to connect with the resolved token. On success `tx` moves into the receive task, so
    // `rx.recv()` yields `None` only when that task drops it (disconnect). On failure `tx` stays
    // owned here, so `rx.recv()` parks and the session ends on the short fallback deadline, letting
    // the outer loop reconnect — pure periodic reconcile covers the gap (poll fallback).
    let (signalr_task, session_secs) =
        match crate::signalr::HubConnection::connect(&server, &token).await {
            Ok(conn) => (Some(spawn_signalr(conn, tx)), SESSION_SECS),
            Err(e) => {
                eprintln!("alert actuation: SignalR connect failed, falling back to polling: {e}");
                (None, RECONNECT_SECS)
            }
        };

    // Reconcile immediately on (re)connect, reusing the token already resolved for `connect`.
    reconcile_with(client, device_id, actuating, runtime, Some((server, token))).await;

    let mut ticker = tokio::time::interval(Duration::from_secs(RECONCILE_SECS));
    ticker.tick().await; // consume the immediate first tick.

    // Bound the session so the token gets refreshed and (if dropped) the connection re-establishes.
    let session_deadline = tokio::time::Instant::now() + Duration::from_secs(session_secs);

    loop {
        tokio::select! {
            _ = ticker.tick() => {
                reconcile(client, device_id, actuating, runtime).await;
            }
            nudge = rx.recv() => {
                match nudge {
                    // A device_action arrived: reconcile now (idempotent against the periodic poll).
                    Some(()) => reconcile(client, device_id, actuating, runtime).await,
                    // SignalR task ended (disconnect). Leave the session to reconnect.
                    None => break,
                }
            }
            _ = tokio::time::sleep_until(session_deadline) => break,
        }
    }

    if let Some(task) = signalr_task {
        task.abort();
    }
}

/// Spawns the SignalR receive pump. Sends `()` on `tx` for each `device_action`; the task ends (and
/// drops `tx`, which the session observes as `rx.recv() == None`) when the connection closes.
fn spawn_signalr(
    mut conn: crate::signalr::HubConnection,
    tx: mpsc::Sender<()>,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        loop {
            match conn.recv().await {
                Ok(crate::signalr::HubEvent::DeviceAction) => {
                    // The event itself is just a trigger; reconcile is the source of truth.
                    if tx.send(()).await.is_err() {
                        break;
                    }
                }
                Ok(crate::signalr::HubEvent::Closed) => break,
                Err(e) => {
                    eprintln!("alert actuation: SignalR recv: {e}");
                    break;
                }
            }
        }
    })
}

/// Reconcile that re-resolves `(server, token)` itself — used by the periodic tick and the
/// `device_action` nudge so it rides the same refresh the poller does and picks up a re-link without
/// restarting the session.
async fn reconcile(
    client: &reqwest::Client,
    device_id: &str,
    actuating: &mut HashSet<String>,
    runtime: &tokio::runtime::Handle,
) {
    reconcile_with(client, device_id, actuating, runtime, None).await;
}

/// Diffs the server's active intents against `actuating` and drives toasts: new excursions toast,
/// vanished excursions clear from the set. Idempotent — re-running with an unchanged server state is
/// a no-op. Acknowledged intents are dropped (no re-alarm). `creds` reuses a token already resolved
/// this cycle (the first reconcile of a session); `None` resolves a fresh one.
async fn reconcile_with(
    client: &reqwest::Client,
    device_id: &str,
    actuating: &mut HashSet<String>,
    runtime: &tokio::runtime::Handle,
    creds: Option<(String, String)>,
) {
    let (server, token) = match creds {
        Some(pair) => pair,
        None => match crate::auth_token(client).await {
            Ok(pair) => pair,
            Err(e) => {
                eprintln!("alert actuation: reconcile token: {e}");
                return;
            }
        },
    };

    let intents = match client_devices::active_intents(client, &server, &token, device_id).await {
        Ok(i) => i,
        Err(e) => {
            eprintln!("alert actuation: active-intents: {e}");
            return;
        }
    };

    let active: HashSet<String> = intents
        .iter()
        .filter(|i| i.is_active())
        .map(|i| i.excursion_id.clone())
        .collect();

    // New excursions: in the server's active set but not yet actuating → toast.
    for intent in intents.iter().filter(|i| i.is_active()) {
        if actuating.insert(intent.excursion_id.clone()) {
            show_toast(intent, &server, &token, runtime);
        }
    }

    // Withdrawn excursions: actuating locally but no longer active server-side → clear. Windows owns
    // the toast lifecycle (the alarm scenario persists until dismissed); we drop our dedup record so
    // a later re-open of the same excursion id toasts again.
    actuating.retain(|id| active.contains(id));
}

/// Shows the toast for `intent`, wiring the Acknowledge button to the ack endpoint.
fn show_toast(
    intent: &DeviceActionIntent,
    server: &str,
    token: &str,
    runtime: &tokio::runtime::Handle,
) {
    let ack = AckContext {
        server: server.to_string(),
        token: token.to_string(),
        excursion_id: intent.excursion_id.clone(),
        runtime: runtime.clone(),
    };
    if let Err(e) = toast::show(intent, Some(ack)) {
        eprintln!("alert actuation: toast: {e}");
    }
}

/// A user-facing label for this install: the machine name, else a generic fallback.
fn machine_label() -> String {
    std::env::var("COMPUTERNAME")
        .ok()
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| "Desktop Companion".to_string())
}

fn http_client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .danger_accept_invalid_certs(cfg!(debug_assertions))
        .build()
        .map_err(|e| format!("could not create HTTP client: {e}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::client_devices::DeviceActionIntent;

    fn intent(id: &str, active: bool, acked: bool) -> DeviceActionIntent {
        DeviceActionIntent {
            intent: if active { "opened".into() } else { "resolved".into() },
            excursion_id: id.into(),
            rule_name: "r".into(),
            severity: "warning".into(),
            acknowledged: acked,
            glucose_value: None,
            trend: None,
        }
    }

    // Mirrors reconcile's set diff without the HTTP/toast side effects, to lock in the dedup rules.
    fn diff(actuating: &mut HashSet<String>, intents: &[DeviceActionIntent]) -> Vec<String> {
        let active: HashSet<String> = intents
            .iter()
            .filter(|i| i.is_active())
            .map(|i| i.excursion_id.clone())
            .collect();
        let mut toasted = Vec::new();
        for i in intents.iter().filter(|i| i.is_active()) {
            if actuating.insert(i.excursion_id.clone()) {
                toasted.push(i.excursion_id.clone());
            }
        }
        actuating.retain(|id| active.contains(id));
        toasted
    }

    #[test]
    fn new_excursion_toasts_once_then_is_deduped() {
        let mut set = HashSet::new();
        let intents = vec![intent("a", true, false)];
        assert_eq!(diff(&mut set, &intents), vec!["a".to_string()]);
        // Same active state on the next reconcile → no new toast.
        assert!(diff(&mut set, &intents).is_empty());
    }

    #[test]
    fn resolved_excursion_clears_and_can_reopen() {
        let mut set = HashSet::new();
        diff(&mut set, &[intent("a", true, false)]);
        // Resolved → removed from the actuating set.
        assert!(diff(&mut set, &[intent("a", false, false)]).is_empty());
        assert!(!set.contains("a"));
        // Re-open of the same id toasts again.
        assert_eq!(diff(&mut set, &[intent("a", true, false)]), vec!["a".to_string()]);
    }

    #[test]
    fn acknowledged_excursion_is_not_toasted() {
        let mut set = HashSet::new();
        assert!(diff(&mut set, &[intent("a", true, true)]).is_empty());
        assert!(!set.contains("a"));
    }

    #[test]
    fn closed_while_offline_produces_nothing() {
        // The companion was offline for the whole excursion; the snapshot is empty on reconnect.
        let mut set = HashSet::new();
        assert!(diff(&mut set, &[]).is_empty());
    }
}
