//! Realtime glucose updates over Nocturne's SignalR DataHub, as a low-latency supplement to the
//! 60s poll in `main.rs`. On any data event the hub nudges the poll loop to refresh immediately;
//! the poll loop stays the fallback whenever the hub is down.
//!
//! Connecting/reconnecting is supervised here rather than by the crate: `authenticate_bearer` fixes
//! the access token at connect time, so each attempt fetches a freshly-refreshed token — otherwise a
//! reconnect after the token expired would fail forever. Registering a `DisconnectionHandler` also
//! tells the crate not to auto-reconnect, leaving this loop in sole control.

use signalr_client::{DisconnectionHandler, ReconnectionHandler, SignalRClient};
use tokio::sync::mpsc;

/// Client name reported to the hub's Authorize method (informational; mirrors the OAuth client).
const HUB_CLIENT: &str = "Nocturne Companion";

/// DataHub route, relative to the tenant base URL.
const HUB: &str = "hubs/data";

/// Collections to subscribe to: the native V4 categories plus the legacy v1 collections, so realtime
/// fires whether the tenant writes V4 or legacy shapes. Matches the Windows widget.
const COLLECTIONS: [&str; 6] = ["glucose", "care", "device", "entries", "treatments", "devicestatus"];

/// Server events that mean "data changed"; each nudges an immediate poll. The companion re-fetches
/// the V4 summary over HTTP rather than consuming payloads, so the event name is all we need.
const DATA_EVENTS: [&str; 5] = ["dataUpdate", "create", "update", "delete", "trackerUpdate"];

/// Delay before re-establishing the hub after it drops or a connect attempt fails.
const RECONNECT_DELAY_SECS: u64 = 5;

/// How often to actively probe the connection for liveness. The crate has no keep-alive of its own
/// and only reports a drop on an explicit close frame, so a silent death (sleep/resume, Wi-Fi drop,
/// idle NAT timeout) would otherwise go unnoticed and park the supervisor forever.
const HEARTBEAT_SECS: u64 = 30;

/// If a liveness probe doesn't round-trip within this long, count it as a failure.
const PROBE_TIMEOUT_SECS: u64 = 15;

/// Reconnect only after this many *consecutive* failed probes, so transient server slowness isn't
/// mistaken for a dead socket (which would turn latency spikes into a reconnect storm).
const PROBE_FAILURE_LIMIT: u32 = 2;

/// Wakes the supervisor when the hub connection drops.
struct DropSignal {
    tx: mpsc::UnboundedSender<()>,
}

impl DisconnectionHandler for DropSignal {
    fn on_disconnected(&self, _reconnection: ReconnectionHandler) {
        let _ = self.tx.send(());
    }
}

/// Spawns the realtime hub supervisor. `refresh_tx` nudges the poll loop in `main.rs`; a full channel
/// means a refresh is already pending, so a dropped nudge coalesces harmlessly.
pub fn spawn(http: reqwest::Client, refresh_tx: mpsc::Sender<()>) {
    tauri::async_runtime::spawn(async move {
        loop {
            if crate::auth::is_linked() {
                if let Err(e) = connect_and_run(&http, &refresh_tx).await {
                    eprintln!("glucose hub: {e}");
                }
            }
            tokio::time::sleep(std::time::Duration::from_secs(RECONNECT_DELAY_SECS)).await;
        }
    });
}

/// Connects, authorizes, subscribes, and runs until the connection drops. Returns `Ok` on a clean
/// drop and `Err` on a connect/auth failure; either way the supervisor retries after a short delay.
async fn connect_and_run(http: &reqwest::Client, refresh_tx: &mpsc::Sender<()>) -> Result<(), String> {
    let (api_url, token) = crate::auth::get_valid_token(http).await?;
    let parsed = url::Url::parse(&api_url).map_err(|e| format!("bad server URL: {e}"))?;
    let host = parsed.host_str().ok_or("server URL has no host")?.to_string();
    let secure = parsed.scheme() == "https";
    let port = parsed.port_or_known_default();

    // The disconnection handler signals this channel; recv below parks the task until the drop.
    let (drop_tx, mut drop_rx) = mpsc::unbounded_channel::<()>();

    let mut hub = SignalRClient::connect_with(&host, HUB, |c| {
        if secure { c.secure(); } else { c.unsecure(); }
        if let Some(p) = port { c.with_port(p as i32); }
        c.authenticate_bearer(token.clone());
        c.with_disconnection_handler(DropSignal { tx: drop_tx.clone() });
    })
    .await?;

    // Register the data-event handlers; keep the handles for the connection's lifetime (dropping a
    // handle unregisters its callback).
    let mut handles = Vec::new();
    for target in DATA_EVENTS {
        let tx = refresh_tx.clone();
        handles.push(hub.register(target.to_string(), move |_ctx| {
            let _ = tx.try_send(());
        }));
    }

    // Join the tenant "authorized" group (legacy entries) and subscribe to the record collections.
    // Both are per-connection, so they run on every (re)connect. Failures leave the connection alive
    // but subscribed to nothing, so surface them rather than silently degrading to poll-only.
    let authorized = hub
        .invoke_with_args::<serde_json::Value, _>("Authorize".to_string(), |c| {
            c.argument(serde_json::json!({ "client": HUB_CLIENT, "token": token.clone() }));
        })
        .await?;
    if !authorized.get("success").and_then(|v| v.as_bool()).unwrap_or(false) {
        eprintln!("glucose hub: data hub rejected authorization; realtime updates will not be received");
    }

    let subscribed = hub
        .invoke_with_args::<serde_json::Value, _>("Subscribe".to_string(), |c| {
            c.argument(serde_json::json!({ "collections": COLLECTIONS }));
        })
        .await?;
    if !subscribed.get("success").and_then(|v| v.as_bool()).unwrap_or(false) {
        eprintln!("glucose hub: data hub rejected subscription; realtime updates will not be received");
    }

    // Catch up immediately: readings may have arrived before we subscribed.
    let _ = refresh_tx.try_send(());

    // Stay until the connection dies, then return so the supervisor reconnects with a fresh token.
    // DropSignal covers clean closes; the heartbeat covers silent deaths the crate can't detect by
    // re-invoking Subscribe — a round-trip through the hub, and idempotent (DataHub.Subscribe just
    // re-adds this connection to its groups). A probe that errors or hangs past the timeout is only
    // treated as death after PROBE_FAILURE_LIMIT consecutive failures, so a slow server doesn't cause
    // a reconnect storm. Delay missed-tick behavior keeps a slow probe from immediately re-firing.
    // `handles`/`hub` stay alive across this loop.
    let mut heartbeat = tokio::time::interval(std::time::Duration::from_secs(HEARTBEAT_SECS));
    heartbeat.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    heartbeat.tick().await; // the first tick completes immediately; skip it
    let mut consecutive_failures: u32 = 0;
    loop {
        tokio::select! {
            _ = drop_rx.recv() => break,
            _ = heartbeat.tick() => {
                let probe = hub.invoke_with_args::<serde_json::Value, _>("Subscribe".to_string(), |c| {
                    c.argument(serde_json::json!({ "collections": COLLECTIONS }));
                });
                let alive = matches!(
                    tokio::time::timeout(std::time::Duration::from_secs(PROBE_TIMEOUT_SECS), probe).await,
                    Ok(Ok(_))
                );
                if alive {
                    consecutive_failures = 0;
                } else {
                    consecutive_failures += 1;
                    if consecutive_failures >= PROBE_FAILURE_LIMIT {
                        eprintln!(
                            "glucose hub: {consecutive_failures} consecutive liveness probes failed; reconnecting"
                        );
                        break;
                    }
                }
            }
        }
    }
    Ok(())
}
