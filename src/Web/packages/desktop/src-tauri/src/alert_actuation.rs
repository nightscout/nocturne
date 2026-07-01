//! Device-action actuation loop: register, listen for `device_action` over SignalR, and reconcile
//! against the active-intents snapshot to drive Windows toasts and tray-icon flashing.
//!
//! The active-intents snapshot is the source of truth, not the live event. A live `device_action`
//! only triggers an immediate reconcile; the reconcile diffs the server's active set against
//! in-memory per-capability "currently actuating" sets keyed on excursion id, so it is idempotent (a
//! live event overlapping the periodic poll cannot double-actuate) and self-healing (an alert closed
//! while the companion was offline simply never appears, producing nothing). Acknowledged intents are
//! skipped. Each active intent actuates the subset of capabilities the server narrowed it to
//! (`notify` → toast, `tray_flash` → flash), so an intent can drive both. Reconcile runs on connect,
//! on every `device_action`, and on a ~30s interval.
//!
//! Transport is the hand-rolled `signalr` client; if a connection attempt fails the loop falls back
//! to pure periodic reconcile until the next reconnect succeeds, so actuation degrades to polling
//! rather than stopping.

use crate::client_devices::{self, DeviceActionIntent};
use crate::signalr::DeviceNotificationMirror;
use crate::toast::{self, AckContext};
use crate::tray;
use std::collections::{HashSet, VecDeque};
use std::time::Duration;
use tokio::sync::mpsc;

/// Cap on the recently-seen notification-id set — enough to absorb a redelivery burst without
/// growing unbounded. Oldest ids are evicted first.
const NOTIFICATION_DEDUP_CAP: usize = 128;

/// Per-capability dedup sets, keyed on excursion id, so each actuation fires once per excursion and
/// stops when the excursion leaves the active set. Parallel sets (rather than one) because an intent
/// may request only a subset of capabilities, and each is withdrawn independently.
#[derive(Default)]
struct ActuationState {
    /// Excursions currently toasted (`notify`).
    notified: HashSet<String>,
    /// Excursions currently flashing the tray (`tray_flash`).
    flashing: HashSet<String>,
    /// Recently surfaced `device_notification` ids, to drop redeliveries.
    seen_notifications: NotificationDedup,
}

/// Bounded FIFO set of recently-seen notification ids. `insert` returns whether the id is new (i.e.
/// should be surfaced); once at capacity, inserting evicts the oldest id. Unlike the excursion
/// actuation sets, notifications have no withdrawal signal, so the bound (not a reconcile) is what
/// keeps it from growing.
#[derive(Default)]
struct NotificationDedup {
    seen: HashSet<String>,
    order: VecDeque<String>,
}

impl NotificationDedup {
    /// Records `id`; returns `true` if it was not already present (caller should surface it).
    fn insert(&mut self, id: &str) -> bool {
        if !self.seen.insert(id.to_string()) {
            return false;
        }
        self.order.push_back(id.to_string());
        if self.order.len() > NOTIFICATION_DEDUP_CAP {
            if let Some(evicted) = self.order.pop_front() {
                self.seen.remove(&evicted);
            }
        }
        true
    }
}

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
    let install_id = crate::install_id::get_or_create();
    let label = machine_label();
    let runtime = tokio::runtime::Handle::current();

    let mut state = ActuationState::default();

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
        run_session(&app, &client, (server, token), &device_id, &mut state, &runtime).await;
    }
}

/// Runs one actuation session until it should be torn down (connection lost or session age-out).
/// Spawns the SignalR receive task (if it connects) and owns the reconcile timer. `creds` is the
/// `(server, token)` pair the outer loop resolved for registration; it is reused for this session's
/// SignalR connect and first reconcile so a single refresh covers both.
async fn run_session(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    creds: (String, String),
    device_id: &str,
    state: &mut ActuationState,
    runtime: &tokio::runtime::Handle,
) {
    // Channel the SignalR task uses to relay hub events to this loop.
    let (tx, mut rx) = mpsc::channel::<HubMessage>(16);

    let (server, token) = creds;
    // This device's user, for filtering the fan-out `device_notification` events. `None` (unparseable
    // token) makes every mirror fail the equality check, so nothing is surfaced — fail closed.
    let subject_id = crate::auth::subject_from_token(&token);

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
    reconcile_with(app, client, device_id, state, runtime, Some((server, token))).await;

    let mut ticker = tokio::time::interval(Duration::from_secs(RECONCILE_SECS));
    ticker.tick().await; // consume the immediate first tick.

    // Bound the session so the token gets refreshed and (if dropped) the connection re-establishes.
    let session_deadline = tokio::time::Instant::now() + Duration::from_secs(session_secs);

    loop {
        tokio::select! {
            _ = ticker.tick() => {
                reconcile(app, client, device_id, state, runtime).await;
            }
            msg = rx.recv() => {
                match msg {
                    // A device_action arrived: reconcile now (idempotent against the periodic poll).
                    Some(HubMessage::DeviceAction) => reconcile(app, client, device_id, state, runtime).await,
                    // An in-app notification mirror: surface it directly (no snapshot to reconcile).
                    Some(HubMessage::Notification(mirror)) => {
                        handle_notification(&mirror, subject_id.as_deref(), &mut state.seen_notifications);
                    }
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

/// Toasts a `device_notification` mirror if it should be surfaced (see `should_surface_notification`).
fn handle_notification(
    mirror: &DeviceNotificationMirror,
    subject_id: Option<&str>,
    dedup: &mut NotificationDedup,
) {
    if !should_surface_notification(mirror, subject_id, dedup) {
        return;
    }
    if let Err(e) = toast::show_notification(&mirror.notification) {
        eprintln!("alert actuation: notification toast: {e}");
    }
}

/// Whether a mirror should be surfaced: it must be for this device's user (`subject_id`) and not a
/// redelivery. Records the id in `dedup` when accepted. A mirror for another user (multi-user tenant
/// fan-out) or a duplicate id returns `false`. Pure but for the dedup mutation, so it is unit-tested.
fn should_surface_notification(
    mirror: &DeviceNotificationMirror,
    subject_id: Option<&str>,
    dedup: &mut NotificationDedup,
) -> bool {
    if subject_id != Some(mirror.user_id.as_str()) {
        return false;
    }
    dedup.insert(&mirror.notification.id)
}

/// A hub event relayed from the SignalR receive task to the session loop.
enum HubMessage {
    /// A `device_action` fired — reconcile.
    DeviceAction,
    /// A `device_notification` mirror to surface.
    Notification(DeviceNotificationMirror),
}

/// Spawns the SignalR receive pump. Relays each hub event on `tx`; the task ends (and drops `tx`,
/// which the session observes as `rx.recv() == None`) when the connection closes.
fn spawn_signalr(
    mut conn: crate::signalr::HubConnection,
    tx: mpsc::Sender<HubMessage>,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        loop {
            let msg = match conn.recv().await {
                Ok(crate::signalr::HubEvent::DeviceAction) => HubMessage::DeviceAction,
                Ok(crate::signalr::HubEvent::DeviceNotification(mirror)) => {
                    HubMessage::Notification(mirror)
                }
                Ok(crate::signalr::HubEvent::Closed) => break,
                Err(e) => {
                    eprintln!("alert actuation: SignalR recv: {e}");
                    break;
                }
            };
            if tx.send(msg).await.is_err() {
                break;
            }
        }
    })
}

/// Reconcile that re-resolves `(server, token)` itself — used by the periodic tick and the
/// `device_action` nudge so it rides the same refresh the poller does and picks up a re-link without
/// restarting the session.
async fn reconcile(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    device_id: &str,
    state: &mut ActuationState,
    runtime: &tokio::runtime::Handle,
) {
    reconcile_with(app, client, device_id, state, runtime, None).await;
}

/// Diffs the server's active intents against the actuation state and drives each capability: new
/// excursions toast (`notify`) and/or start a tray flash (`tray_flash`); excursions that leave the
/// active set have their toast dedup dropped and their flash stopped. Idempotent — re-running with an
/// unchanged server state is a no-op. Acknowledged intents are dropped (no re-actuation). `creds`
/// reuses a token already resolved this cycle (the first reconcile of a session); `None` resolves a
/// fresh one.
async fn reconcile_with(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    device_id: &str,
    state: &mut ActuationState,
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

    // New actuations per capability: fire once per excursion (HashSet::insert is the dedup gate).
    for intent in intents.iter().filter(|i| i.is_active()) {
        if intent.wants_notify() && state.notified.insert(intent.excursion_id.clone()) {
            show_toast(intent, &server, &token, runtime);
        }
        if intent.wants_tray_flash() && state.flashing.insert(intent.excursion_id.clone()) {
            tray::set_tray_flash(app, &intent.excursion_id, true);
        }
    }

    // Withdrawn excursions: actuating locally but no longer active server-side → clear. Windows owns
    // the toast lifecycle (the alarm scenario persists until dismissed), so notify only drops its
    // dedup record; tray_flash is companion-owned, so each stopped excursion is told to stop and the
    // tray restores the normal icon once the last flash clears. Dropping the dedup record lets a
    // later re-open of the same excursion id actuate again.
    state.notified.retain(|id| active.contains(id));

    let stopped: Vec<String> = state.flashing.difference(&active).cloned().collect();
    for id in stopped {
        state.flashing.remove(&id);
        tray::set_tray_flash(app, &id, false);
    }
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
    use crate::client_devices::{DeviceActionIntent, NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY};

    fn intent(id: &str, active: bool, acked: bool, caps: &[&str]) -> DeviceActionIntent {
        DeviceActionIntent {
            intent: if active { "opened".into() } else { "resolved".into() },
            excursion_id: id.into(),
            rule_name: "r".into(),
            severity: "warning".into(),
            capabilities: caps.iter().map(|c| c.to_string()).collect(),
            acknowledged: acked,
            glucose_value: None,
            trend: None,
        }
    }

    /// What one reconcile actuated, mirroring `reconcile_with`'s per-capability diff without the HTTP
    /// or OS side effects — the pure dedup logic under test.
    #[derive(Default, Debug, PartialEq, Eq)]
    struct Effects {
        toasted: Vec<String>,
        flash_started: Vec<String>,
        flash_stopped: Vec<String>,
    }

    fn reconcile_effects(state: &mut ActuationState, intents: &[DeviceActionIntent]) -> Effects {
        let active: HashSet<String> = intents
            .iter()
            .filter(|i| i.is_active())
            .map(|i| i.excursion_id.clone())
            .collect();

        let mut fx = Effects::default();
        for i in intents.iter().filter(|i| i.is_active()) {
            if i.wants_notify() && state.notified.insert(i.excursion_id.clone()) {
                fx.toasted.push(i.excursion_id.clone());
            }
            if i.wants_tray_flash() && state.flashing.insert(i.excursion_id.clone()) {
                fx.flash_started.push(i.excursion_id.clone());
            }
        }

        state.notified.retain(|id| active.contains(id));
        let stopped: Vec<String> = state.flashing.difference(&active).cloned().collect();
        for id in &stopped {
            state.flashing.remove(id);
        }
        fx.flash_stopped = stopped;
        fx
    }

    #[test]
    fn new_excursion_toasts_once_then_is_deduped() {
        let mut state = ActuationState::default();
        let intents = vec![intent("a", true, false, &[NOTIFY_CAPABILITY])];
        assert_eq!(reconcile_effects(&mut state, &intents).toasted, vec!["a".to_string()]);
        // Same active state on the next reconcile → no new toast.
        assert!(reconcile_effects(&mut state, &intents).toasted.is_empty());
    }

    #[test]
    fn resolved_excursion_clears_and_can_reopen() {
        let mut state = ActuationState::default();
        reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]);
        // Resolved → removed from the notified set.
        assert!(reconcile_effects(&mut state, &[intent("a", false, false, &[NOTIFY_CAPABILITY])]).toasted.is_empty());
        assert!(!state.notified.contains("a"));
        // Re-open of the same id toasts again.
        assert_eq!(
            reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]).toasted,
            vec!["a".to_string()]
        );
    }

    #[test]
    fn acknowledged_excursion_is_not_actuated() {
        let mut state = ActuationState::default();
        let fx = reconcile_effects(&mut state, &[intent("a", true, true, &[NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY])]);
        assert!(fx.toasted.is_empty());
        assert!(fx.flash_started.is_empty());
        assert!(!state.notified.contains("a"));
        assert!(!state.flashing.contains("a"));
    }

    #[test]
    fn closed_while_offline_produces_nothing() {
        // The companion was offline for the whole excursion; the snapshot is empty on reconnect.
        let mut state = ActuationState::default();
        assert_eq!(reconcile_effects(&mut state, &[]), Effects::default());
    }

    #[test]
    fn tray_flash_starts_once_then_is_deduped() {
        let mut state = ActuationState::default();
        let intents = vec![intent("a", true, false, &[TRAY_FLASH_CAPABILITY])];
        assert_eq!(reconcile_effects(&mut state, &intents).flash_started, vec!["a".to_string()]);
        // Same active state next reconcile → no restart (don't re-flash every 30s poll).
        let fx = reconcile_effects(&mut state, &intents);
        assert!(fx.flash_started.is_empty());
        assert!(fx.flash_stopped.is_empty());
    }

    #[test]
    fn tray_flash_stops_when_excursion_leaves_active_set() {
        let mut state = ActuationState::default();
        reconcile_effects(&mut state, &[intent("a", true, false, &[TRAY_FLASH_CAPABILITY])]);
        // Resolved → flash stops and the dedup record clears.
        let fx = reconcile_effects(&mut state, &[intent("a", false, false, &[TRAY_FLASH_CAPABILITY])]);
        assert_eq!(fx.flash_stopped, vec!["a".to_string()]);
        assert!(!state.flashing.contains("a"));
        // Re-open flashes again.
        assert_eq!(
            reconcile_effects(&mut state, &[intent("a", true, false, &[TRAY_FLASH_CAPABILITY])]).flash_started,
            vec!["a".to_string()]
        );
    }

    #[test]
    fn intent_with_both_capabilities_actuates_both() {
        let mut state = ActuationState::default();
        let fx = reconcile_effects(
            &mut state,
            &[intent("a", true, false, &[NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY])],
        );
        assert_eq!(fx.toasted, vec!["a".to_string()]);
        assert_eq!(fx.flash_started, vec!["a".to_string()]);
    }

    #[test]
    fn notify_only_intent_does_not_flash() {
        let mut state = ActuationState::default();
        let fx = reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]);
        assert_eq!(fx.toasted, vec!["a".to_string()]);
        assert!(fx.flash_started.is_empty());
        assert!(!state.flashing.contains("a"));
    }

    // ── device_notification: sub-filter + dedup ──────────────────────────────────────────

    fn mirror(user_id: &str, notif_id: &str) -> DeviceNotificationMirror {
        DeviceNotificationMirror {
            user_id: user_id.into(),
            notification: crate::signalr::InAppNotification {
                id: notif_id.into(),
                title: "t".into(),
                subtitle: None,
            },
        }
    }

    #[test]
    fn notification_surfaces_once_for_own_user_then_deduped() {
        let mut dedup = NotificationDedup::default();
        let m = mirror("me", "n1");
        assert!(should_surface_notification(&m, Some("me"), &mut dedup));
        // Redelivery of the same id → dropped.
        assert!(!should_surface_notification(&m, Some("me"), &mut dedup));
    }

    #[test]
    fn notification_for_other_user_is_dropped() {
        let mut dedup = NotificationDedup::default();
        let m = mirror("someone-else", "n1");
        assert!(!should_surface_notification(&m, Some("me"), &mut dedup));
        // A drop for the wrong user must not consume the id — the correct user could still get it.
        assert!(should_surface_notification(&mirror("me", "n1"), Some("me"), &mut dedup));
    }

    #[test]
    fn notification_dropped_when_subject_unknown() {
        // Unparseable token → subject None → fail closed.
        let mut dedup = NotificationDedup::default();
        assert!(!should_surface_notification(&mirror("me", "n1"), None, &mut dedup));
    }

    #[test]
    fn notification_dedup_is_bounded_and_fifo_evicts_oldest() {
        let mut dedup = NotificationDedup::default();
        for i in 0..NOTIFICATION_DEDUP_CAP {
            assert!(dedup.insert(&format!("n{i}")));
        }
        assert_eq!(dedup.order.len(), NOTIFICATION_DEDUP_CAP);
        // One more evicts the oldest ("n0").
        assert!(dedup.insert("overflow"));
        assert_eq!(dedup.order.len(), NOTIFICATION_DEDUP_CAP);
        assert!(!dedup.seen.contains("n0"));
        // The evicted id is treated as new again (acceptable: eviction only happens far past the
        // redelivery window).
        assert!(dedup.insert("n0"));
    }
}
