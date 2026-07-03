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
//! Registration happens once per process and the device id is reused across sessions; it is
//! re-resolved only when the server invalidates it (401/404 on the snapshot, 409 on register) or the
//! credential changes (unlink). A definitive credential loss (unlink/revoked grant) withdraws every
//! active actuation; transient failures keep state so nothing is spuriously withdrawn.
//!
//! Transport is the hand-rolled `signalr` client; if a connection attempt fails the loop falls back
//! to pure periodic reconcile until the next reconnect succeeds, so actuation degrades to polling
//! rather than stopping.
//!
//! The session here owns the app's only SignalR connection: besides the device targets it
//! subscribes to the DataHub's data events and forwards each as a nudge on `refresh_tx`, which the
//! glucose poll loop in `main.rs` consumes for low-latency refreshes. The 60s glucose poll remains
//! the fallback whenever the hub is down, exactly like the reconcile interval is for actuation.

use crate::auth::{self, TokenError};
use crate::client_devices::{self, DeviceActionIntent};
use crate::signalr::DeviceNotificationMirror;
use crate::toast::{self, AckContext};
use crate::tray;
use std::collections::{HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;
use std::time::Duration;
use tokio::sync::{mpsc, Notify};

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
/// Backoff after the server refuses registration (403) — the grant/user cannot register, so retrying
/// at the reconcile cadence buys nothing; `on_link`/`on_unlink` wake the loop out of the backoff so
/// a relink restarts registration promptly.
const REGISTER_BACKOFF_SECS: u64 = 15 * 60;

/// Set while the stored grant cannot drive device alerts: it lacks the device scopes, or the server
/// refuses this install's registration (403). Read by the `companion_link_status` command; cleared
/// on a successful registration or an unlink.
static NEEDS_RELINK: AtomicBool = AtomicBool::new(false);

/// Set by `on_unlink` so the loop drops its per-excursion state and registration on its next pass
/// (the tray flashes are already stopped synchronously by `on_unlink`).
static RESET_STATE: AtomicBool = AtomicBool::new(false);

pub fn needs_relink() -> bool {
    NEEDS_RELINK.load(Ordering::SeqCst)
}

/// Wakes the actuation loop out of any backoff/idle sleep. Signaled by `on_link`/`on_unlink`;
/// `notify_one` stores a permit when no one is waiting, so a wake landing while the loop is
/// mid-pass short-circuits its next sleep instead of being lost.
fn wake() -> &'static Notify {
    static WAKE: OnceLock<Notify> = OnceLock::new();
    WAKE.get_or_init(Notify::new)
}

/// Sleeps up to `secs`, returning early when `notify` is signaled. Every backoff/idle sleep in the
/// registration loop goes through this with the global `wake()`, so a link/unlink restarts the
/// loop promptly instead of waiting out the backoff (up to 15 min after a register 403).
async fn interruptible_sleep(notify: &Notify, secs: u64) {
    let _ = tokio::time::timeout(Duration::from_secs(secs), notify.notified()).await;
}

/// Called when the user unlinks: withdraws every actuation immediately (stops all flashes and
/// repaints the tray), flags the actuation loop to reset its state and registration, and wakes it
/// out of any backoff.
pub fn on_unlink(app: &tauri::AppHandle) {
    reset_registration(app);
}

/// Called when a device-flow link completes — with or without a prior unlink. A relink that
/// replaces the stored credentials in place must reset too: any active actuations and the
/// registration belong to the previous credential/server, and the loop may be sitting in a
/// register backoff that would otherwise run out before the new credential is tried.
pub fn on_link(app: &tauri::AppHandle) {
    reset_registration(app);
}

fn reset_registration(app: &tauri::AppHandle) {
    NEEDS_RELINK.store(false, Ordering::SeqCst);
    RESET_STATE.store(true, Ordering::SeqCst);
    tray::stop_all_flashes(app);
    wake().notify_one();
}

/// Drives registration + actuation for as long as the app runs. Idles (retrying) while unlinked,
/// resolving `(server, token)` via the same durable-OAuth path the glucose poller uses.
/// `refresh_tx` nudges the glucose poll loop on every subscribed data event (a full channel means
/// a refresh is already pending, so a dropped nudge coalesces harmlessly).
pub async fn run(app: tauri::AppHandle, refresh_tx: mpsc::Sender<()>) {
    let mut install_id = crate::install_id::get_or_create();
    let label = machine_label();
    let runtime = tokio::runtime::Handle::current();

    let client = match crate::http::client() {
        Ok(c) => c,
        Err(e) => {
            eprintln!("alert actuation: {e}");
            return;
        }
    };

    let mut state = ActuationState::default();
    // Registration is per-process: kept across sessions, dropped only when invalidated.
    let mut device_id: Option<String> = None;

    loop {
        if RESET_STATE.swap(false, Ordering::SeqCst) {
            clear_actuations(&mut state);
            device_id = None;
        }

        // A grant without the device scopes would 403 on register forever (pre-upgrade tokens:
        // refresh never re-scopes), so surface the relink-needed state and idle instead of hammering
        // the endpoint. Only a relink changes the stored scopes.
        if let Some(scopes) = auth::granted_scopes() {
            if !scopes.is_empty() && !has_device_scopes(&scopes) {
                NEEDS_RELINK.store(true, Ordering::SeqCst);
                interruptible_sleep(wake(), RECONCILE_SECS).await;
                continue;
            }
        }

        let (server, token) = match auth::get_valid_token(&client).await {
            Ok(pair) => pair,
            Err(TokenError::NotLinked(_)) => {
                // Definitively unlinked/revoked: withdraw everything and idle until a relink.
                withdraw_all(&app, &mut state);
                device_id = None;
                interruptible_sleep(wake(), RECONCILE_SECS).await;
                continue;
            }
            Err(TokenError::Transient(e)) => {
                eprintln!("alert actuation: token: {e}");
                interruptible_sleep(wake(), RECONCILE_SECS).await;
                continue;
            }
        };

        let session_device_id = match &device_id {
            Some(id) => id.clone(),
            None => match register_install(&client, &server, &token, &mut install_id, &label).await {
                Ok(id) => {
                    NEEDS_RELINK.store(false, Ordering::SeqCst);
                    device_id = Some(id.clone());
                    id
                }
                Err(retry_secs) => {
                    interruptible_sleep(wake(), retry_secs).await;
                    continue;
                }
            },
        };

        // One session: hold a SignalR connection (best-effort) and reconcile on connect, on each
        // device_action, and on the periodic tick. Returns when the session ages out, the connection
        // drops, or the registration/credential is invalidated. The pair just resolved above is
        // reused for the session's connect + first reconcile.
        match run_session(
            &app,
            &client,
            (server, token),
            &session_device_id,
            &mut state,
            &runtime,
            &refresh_tx,
        )
        .await
        {
            SessionEnd::Recycle => {}
            SessionEnd::InvalidateRegistration => device_id = None,
        }
    }
}

/// What the registration loop should do about a failed register, by HTTP status.
#[derive(Debug, PartialEq, Eq)]
enum RegisterRecovery {
    /// 409: the install id is registered to another subject — regenerate the id and retry once.
    RegenerateId,
    /// 403: this user/grant may not register actuation devices — back off and surface needs-relink.
    NeedsRelink,
    /// Anything else — transient; retry at the normal cadence.
    Retry,
}

fn classify_register_error(status: Option<u16>) -> RegisterRecovery {
    match status {
        Some(409) => RegisterRecovery::RegenerateId,
        Some(403) => RegisterRecovery::NeedsRelink,
        _ => RegisterRecovery::Retry,
    }
}

/// Registers this install, recovering from a 409 (install id owned by another subject — the old
/// device row stays until its owner revokes it) by regenerating the id and retrying once. Returns
/// the server device id, or how many seconds the caller should wait before the next attempt. Sets
/// `NEEDS_RELINK` when the server refuses with 403.
async fn register_install(
    client: &reqwest::Client,
    server: &str,
    token: &str,
    install_id: &mut String,
    label: &str,
) -> Result<String, u64> {
    let mut regenerated = false;
    loop {
        match client_devices::register(client, server, token, install_id, label).await {
            Ok(id) => return Ok(id),
            Err(e) => {
                eprintln!("alert actuation: register: {e}");
                match classify_register_error(e.status) {
                    RegisterRecovery::RegenerateId if !regenerated => {
                        regenerated = true;
                        *install_id = crate::install_id::regenerate();
                    }
                    RegisterRecovery::NeedsRelink => {
                        NEEDS_RELINK.store(true, Ordering::SeqCst);
                        return Err(REGISTER_BACKOFF_SECS);
                    }
                    _ => return Err(RECONCILE_SECS),
                }
            }
        }
    }
}

/// Whether `scopes` includes both scopes device actuation needs (`device.notify` for toasts,
/// `device.actuate` for tray flash). An empty list means the server did not echo scopes — the caller
/// treats that as unknown, not missing.
fn has_device_scopes(scopes: &[String]) -> bool {
    ["device.notify", "device.actuate"]
        .iter()
        .all(|required| scopes.iter().any(|s| s == required))
}

/// Why a session ended: `Recycle` reconnects with the same registration; `InvalidateRegistration`
/// makes the outer loop re-register (device id rejected, or the credential is gone).
enum SessionEnd {
    Recycle,
    InvalidateRegistration,
}

/// Runs one actuation session until it should be torn down (connection lost, session age-out, or the
/// registration/credential invalidated). Spawns the SignalR receive task (if it connects) and owns
/// the reconcile timer. `creds` is the `(server, token)` pair the outer loop resolved for
/// registration; it is reused for this session's SignalR connect and first reconcile so a single
/// refresh covers both.
async fn run_session(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    creds: (String, String),
    device_id: &str,
    state: &mut ActuationState,
    runtime: &tokio::runtime::Handle,
    refresh_tx: &mpsc::Sender<()>,
) -> SessionEnd {
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
        match crate::signalr::HubConnection::connect(&server, &token, &crate::signalr::DATA_EVENTS)
            .await
        {
            Ok(conn) => {
                // Catch up immediately: readings may have arrived while disconnected, before the
                // Subscribe on this connection took effect.
                let _ = refresh_tx.try_send(());
                (Some(spawn_signalr(conn, tx, refresh_tx.clone())), SESSION_SECS)
            }
            Err(e) => {
                eprintln!("alert actuation: SignalR connect failed, falling back to polling: {e}");
                (None, RECONNECT_SECS)
            }
        };

    let mut end = SessionEnd::Recycle;

    // Reconcile immediately on (re)connect, reusing the token already resolved for `connect`.
    if ends_session(reconcile_with(app, client, device_id, state, runtime, Some((server, token))).await)
    {
        end = SessionEnd::InvalidateRegistration;
    } else {
        let mut ticker = tokio::time::interval(Duration::from_secs(RECONCILE_SECS));
        ticker.tick().await; // consume the immediate first tick.

        // Bound the session so the token gets refreshed and (if dropped) the connection re-establishes.
        let session_deadline = tokio::time::Instant::now() + Duration::from_secs(session_secs);

        loop {
            let outcome = tokio::select! {
                _ = ticker.tick() => {
                    Some(reconcile(app, client, device_id, state, runtime).await)
                }
                msg = rx.recv() => {
                    match msg {
                        // A device_action arrived: reconcile now (idempotent against the periodic poll).
                        Some(HubMessage::DeviceAction) => {
                            Some(reconcile(app, client, device_id, state, runtime).await)
                        }
                        // An in-app notification mirror: surface it directly (no snapshot to reconcile).
                        Some(HubMessage::Notification(mirror)) => {
                            handle_notification(&mirror, subject_id.as_deref(), &mut state.seen_notifications);
                            None
                        }
                        // SignalR task ended (disconnect). Leave the session to reconnect.
                        None => break,
                    }
                }
                _ = tokio::time::sleep_until(session_deadline) => break,
                // Link/unlink: end the session now; the outer loop applies the RESET_STATE reset
                // (fresh credentials, dropped registration) on its next pass.
                _ = wake().notified() => break,
            };

            if outcome.is_some_and(ends_session) {
                end = SessionEnd::InvalidateRegistration;
                break;
            }
        }
    }

    if let Some(task) = signalr_task {
        task.abort();
    }
    end
}

/// Whether a reconcile outcome should tear the session down and re-resolve registration.
fn ends_session(outcome: ReconcileOutcome) -> bool {
    matches!(
        outcome,
        ReconcileOutcome::NotLinked | ReconcileOutcome::InvalidRegistration
    )
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

/// Spawns the SignalR receive pump. Relays each device event on `tx`; a subscribed data event
/// nudges `refresh_tx` directly (the session loop has nothing to do with it, and skipping the relay
/// keeps the hub-event → glucose-refresh latency at one hop). The task ends (and drops `tx`, which
/// the session observes as `rx.recv() == None`) when the connection closes.
fn spawn_signalr(
    mut conn: crate::signalr::HubConnection,
    tx: mpsc::Sender<HubMessage>,
    refresh_tx: mpsc::Sender<()>,
) -> tokio::task::JoinHandle<()> {
    tokio::spawn(async move {
        loop {
            let msg = match conn.recv().await {
                Ok(crate::signalr::HubEvent::DeviceAction) => HubMessage::DeviceAction,
                Ok(crate::signalr::HubEvent::DeviceNotification(mirror)) => {
                    HubMessage::Notification(mirror)
                }
                Ok(crate::signalr::HubEvent::Named) => {
                    let _ = refresh_tx.try_send(());
                    continue;
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

/// Outcome of one reconcile pass, for the session/outer loop to react to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum ReconcileOutcome {
    Ok,
    /// Credentials are definitively gone/revoked; all actuations were withdrawn.
    NotLinked,
    /// The server rejected the device id (401/404) — re-register.
    InvalidRegistration,
    /// Transient failure; actuation state kept so nothing is spuriously withdrawn.
    Transient,
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
) -> ReconcileOutcome {
    reconcile_with(app, client, device_id, state, runtime, None).await
}

/// Fetches the active-intents snapshot and applies the per-capability diff (`reconcile_effects`):
/// new excursions toast (`notify`) and/or start a tray flash (`tray_flash`); excursions that leave
/// the active set have their toast dedup dropped and their flash stopped. Idempotent — re-running
/// with an unchanged server state is a no-op. A definitive credential loss withdraws every actuation;
/// transient failures keep state. `creds` reuses a token already resolved this cycle (the first
/// reconcile of a session); `None` resolves a fresh one.
async fn reconcile_with(
    app: &tauri::AppHandle,
    client: &reqwest::Client,
    device_id: &str,
    state: &mut ActuationState,
    runtime: &tokio::runtime::Handle,
    creds: Option<(String, String)>,
) -> ReconcileOutcome {
    let (server, token) = match creds {
        Some(pair) => pair,
        None => match auth::get_valid_token(client).await {
            Ok(pair) => pair,
            Err(TokenError::NotLinked(_)) => {
                withdraw_all(app, state);
                return ReconcileOutcome::NotLinked;
            }
            Err(TokenError::Transient(e)) => {
                eprintln!("alert actuation: reconcile token: {e}");
                return ReconcileOutcome::Transient;
            }
        },
    };

    let intents = match client_devices::active_intents(client, &server, &token, device_id).await {
        Ok(i) => i,
        Err(e) => {
            eprintln!("alert actuation: active-intents: {e}");
            return match e.status {
                Some(401) | Some(404) => ReconcileOutcome::InvalidRegistration,
                _ => ReconcileOutcome::Transient,
            };
        }
    };

    let effects = reconcile_effects(state, &intents);
    for intent in &effects.toast {
        show_toast(intent, &server, runtime);
    }
    for id in &effects.flash_start {
        tray::set_tray_flash(app, id, true);
    }
    for id in &effects.flash_stop {
        tray::set_tray_flash(app, id, false);
    }
    ReconcileOutcome::Ok
}

/// What one reconcile pass actuates/withdraws — the pure per-capability diff of the server's active
/// intents against the local actuation state. Shared by `reconcile_with` and the unit tests, so the
/// algorithm under test is the one that ships.
#[derive(Default, Debug, PartialEq)]
struct ReconcileEffects {
    /// Intents to toast (`notify` newly active).
    toast: Vec<DeviceActionIntent>,
    /// Excursions whose tray flash starts.
    flash_start: Vec<String>,
    /// Excursions whose tray flash stops.
    flash_stop: Vec<String>,
}

fn reconcile_effects(state: &mut ActuationState, intents: &[DeviceActionIntent]) -> ReconcileEffects {
    let active: HashSet<String> = intents
        .iter()
        .filter(|i| i.is_active())
        .map(|i| i.excursion_id.clone())
        .collect();

    let mut effects = ReconcileEffects::default();

    // New actuations per capability: fire once per excursion (HashSet::insert is the dedup gate).
    for intent in intents.iter().filter(|i| i.is_active()) {
        if intent.wants_notify() && state.notified.insert(intent.excursion_id.clone()) {
            effects.toast.push(intent.clone());
        }
        if intent.wants_tray_flash() && state.flashing.insert(intent.excursion_id.clone()) {
            effects.flash_start.push(intent.excursion_id.clone());
        }
    }

    // Withdrawn excursions: actuating locally but no longer active server-side → clear. Windows owns
    // the toast lifecycle (the alarm scenario persists until dismissed), so notify only drops its
    // dedup record; tray_flash is companion-owned, so each stopped excursion is reported for an
    // explicit stop. Dropping the dedup record lets a later re-open of the same excursion id actuate
    // again.
    state.notified.retain(|id| active.contains(id));
    effects.flash_stop = state.flashing.difference(&active).cloned().collect();
    for id in &effects.flash_stop {
        state.flashing.remove(id);
    }

    effects
}

/// Withdraws every active actuation: clears the per-excursion state and stops all tray flashes
/// (restoring the normal icon). For definitive credential loss (unlink/revoked) — a transient
/// failure must NOT come through here.
fn withdraw_all(app: &tauri::AppHandle, state: &mut ActuationState) {
    clear_actuations(state);
    tray::stop_all_flashes(app);
}

/// Clears the per-excursion actuation sets. The notification dedup is kept — redelivery suppression
/// stays valid across a relink.
fn clear_actuations(state: &mut ActuationState) {
    state.notified.clear();
    state.flashing.clear();
}

/// Shows the toast for `intent`, wiring the Acknowledge button to the ack endpoint. The ack context
/// carries no token — the toast can outlive it; a fresh one is resolved at click time.
fn show_toast(intent: &DeviceActionIntent, server: &str, runtime: &tokio::runtime::Handle) {
    let ack = AckContext {
        server: server.to_string(),
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::client_devices::{NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY};

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

    impl ReconcileEffects {
        fn toasted_ids(&self) -> Vec<&str> {
            self.toast.iter().map(|i| i.excursion_id.as_str()).collect()
        }
    }

    #[test]
    fn new_excursion_toasts_once_then_is_deduped() {
        let mut state = ActuationState::default();
        let intents = vec![intent("a", true, false, &[NOTIFY_CAPABILITY])];
        assert_eq!(reconcile_effects(&mut state, &intents).toasted_ids(), vec!["a"]);
        // Same active state on the next reconcile → no new toast.
        assert!(reconcile_effects(&mut state, &intents).toast.is_empty());
    }

    #[test]
    fn resolved_excursion_clears_and_can_reopen() {
        let mut state = ActuationState::default();
        reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]);
        // Resolved → removed from the notified set.
        assert!(reconcile_effects(&mut state, &[intent("a", false, false, &[NOTIFY_CAPABILITY])]).toast.is_empty());
        assert!(!state.notified.contains("a"));
        // Re-open of the same id toasts again.
        assert_eq!(
            reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]).toasted_ids(),
            vec!["a"]
        );
    }

    #[test]
    fn acknowledged_excursion_is_not_actuated() {
        let mut state = ActuationState::default();
        let fx = reconcile_effects(&mut state, &[intent("a", true, true, &[NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY])]);
        assert!(fx.toast.is_empty());
        assert!(fx.flash_start.is_empty());
        assert!(!state.notified.contains("a"));
        assert!(!state.flashing.contains("a"));
    }

    #[test]
    fn closed_while_offline_produces_nothing() {
        // The companion was offline for the whole excursion; the snapshot is empty on reconnect.
        let mut state = ActuationState::default();
        assert_eq!(reconcile_effects(&mut state, &[]), ReconcileEffects::default());
    }

    #[test]
    fn tray_flash_starts_once_then_is_deduped() {
        let mut state = ActuationState::default();
        let intents = vec![intent("a", true, false, &[TRAY_FLASH_CAPABILITY])];
        assert_eq!(reconcile_effects(&mut state, &intents).flash_start, vec!["a".to_string()]);
        // Same active state next reconcile → no restart (don't re-flash every 30s poll).
        let fx = reconcile_effects(&mut state, &intents);
        assert!(fx.flash_start.is_empty());
        assert!(fx.flash_stop.is_empty());
    }

    #[test]
    fn tray_flash_stops_when_excursion_leaves_active_set() {
        let mut state = ActuationState::default();
        reconcile_effects(&mut state, &[intent("a", true, false, &[TRAY_FLASH_CAPABILITY])]);
        // Resolved → flash stops and the dedup record clears.
        let fx = reconcile_effects(&mut state, &[intent("a", false, false, &[TRAY_FLASH_CAPABILITY])]);
        assert_eq!(fx.flash_stop, vec!["a".to_string()]);
        assert!(!state.flashing.contains("a"));
        // Re-open flashes again.
        assert_eq!(
            reconcile_effects(&mut state, &[intent("a", true, false, &[TRAY_FLASH_CAPABILITY])]).flash_start,
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
        assert_eq!(fx.toasted_ids(), vec!["a"]);
        assert_eq!(fx.flash_start, vec!["a".to_string()]);
    }

    #[test]
    fn notify_only_intent_does_not_flash() {
        let mut state = ActuationState::default();
        let fx = reconcile_effects(&mut state, &[intent("a", true, false, &[NOTIFY_CAPABILITY])]);
        assert_eq!(fx.toasted_ids(), vec!["a"]);
        assert!(fx.flash_start.is_empty());
        assert!(!state.flashing.contains("a"));
    }

    #[test]
    fn withdraw_all_clears_state_and_next_reconcile_reactuates() {
        let mut state = ActuationState::default();
        let intents = vec![intent("a", true, false, &[NOTIFY_CAPABILITY, TRAY_FLASH_CAPABILITY])];
        reconcile_effects(&mut state, &intents);
        assert!(state.notified.contains("a"));
        assert!(state.flashing.contains("a"));

        // Definitive credential loss withdraws everything…
        clear_actuations(&mut state);
        assert!(state.notified.is_empty());
        assert!(state.flashing.is_empty());

        // …and a later relink with the excursion still active re-actuates both capabilities.
        let fx = reconcile_effects(&mut state, &intents);
        assert_eq!(fx.toasted_ids(), vec!["a"]);
        assert_eq!(fx.flash_start, vec!["a".to_string()]);
    }

    #[test]
    fn register_errors_classify_by_status() {
        assert_eq!(classify_register_error(Some(409)), RegisterRecovery::RegenerateId);
        assert_eq!(classify_register_error(Some(403)), RegisterRecovery::NeedsRelink);
        assert_eq!(classify_register_error(Some(500)), RegisterRecovery::Retry);
        // No response at all (network) is transient.
        assert_eq!(classify_register_error(None), RegisterRecovery::Retry);
    }

    #[test]
    fn device_scope_check_requires_both_scopes() {
        let full: Vec<String> = ["glucose.read", "device.notify", "device.actuate"]
            .iter().map(|s| s.to_string()).collect();
        assert!(has_device_scopes(&full));
        // A pre-upgrade grant (read scopes only) lacks them.
        let stale: Vec<String> = ["glucose.read", "therapy.read"].iter().map(|s| s.to_string()).collect();
        assert!(!has_device_scopes(&stale));
        // One of the two is not enough.
        let partial: Vec<String> = ["device.notify"].iter().map(|s| s.to_string()).collect();
        assert!(!has_device_scopes(&partial));
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

    // ── interruptible backoff ─────────────────────────────────────────────────────────────

    // Local `Notify` instances (not the global `wake()`) so parallel tests can't steal each
    // other's permits.

    #[tokio::test]
    async fn pending_wake_short_circuits_backoff_sleep() {
        // A permit stored before the sleep begins (link/unlink landed while the loop was mid-pass)
        // must not be lost: the register backoff returns immediately instead of waiting 15 min.
        let notify = Notify::new();
        notify.notify_one();
        tokio::time::timeout(
            Duration::from_secs(5),
            interruptible_sleep(&notify, REGISTER_BACKOFF_SECS),
        )
        .await
        .expect("a pending wake should short-circuit the backoff");
    }

    #[tokio::test]
    async fn wake_during_backoff_interrupts_sleep() {
        let notify = std::sync::Arc::new(Notify::new());
        let sleeper = {
            let notify = notify.clone();
            tokio::spawn(async move { interruptible_sleep(&notify, REGISTER_BACKOFF_SECS).await })
        };
        // Let the sleeper start waiting, then wake it (the relink path).
        tokio::time::sleep(Duration::from_millis(50)).await;
        notify.notify_one();
        tokio::time::timeout(Duration::from_secs(5), sleeper)
            .await
            .expect("a wake mid-backoff should interrupt the sleep")
            .unwrap();
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
