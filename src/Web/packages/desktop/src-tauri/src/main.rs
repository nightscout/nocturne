// Nocturne desktop companion. Feature #1: CareLink connect.
//
// Medtronic's Auth0 client only allows the com.medtronic.carepartner:/sso custom-scheme
// redirect and login is CAPTCHA-gated, so a plain web flow cannot receive the auth code.
// This app opens the CareLink login in its own webview, lets the user solve the captcha,
// intercepts the custom-scheme redirect via on_navigation, and relays the captured code
// to the Nocturne server, which owns the PKCE exchange and stores the connector secrets.
//
// The app authenticates to Nocturne with a short-lived link code minted by the web UI
// (nocturne-connect://link?server=…&token=…); the token is a tenant-pinned bearer accepted
// only by the CareLink connect endpoints.

#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod auth;
mod glucose_file;
mod glucose_poll;
mod tray;

use serde::{Deserialize, Serialize};
use std::sync::Mutex;
use tauri::{Emitter, Manager, State};
use url::Url;

const LOGIN_WINDOW_LABEL: &str = "carelink-login";
const FLOATING_WINDOW_LABEL: &str = "floating-clock";
// Match on the scheme prefix, not ":/sso" — the observed redirect uses a single slash.
const REDIRECT_SCHEME_PREFIX: &str = "com.medtronic.carepartner:";

#[derive(Default)]
struct Session {
    server_url: Option<String>,
    token: Option<String>,
    flow_state: Option<String>,
}

type SessionState = Mutex<Session>;

/// Error shape returned to the frontend; `status` carries the upstream HTTP status when the
/// failure came from the Nocturne API (401 ⇒ the link code expired).
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct CommandError {
    status: Option<u16>,
    message: String,
}

impl CommandError {
    fn new(message: impl Into<String>) -> Self {
        Self { status: None, message: message.into() }
    }

    fn http(status: u16, message: impl Into<String>) -> Self {
        Self { status: Some(status), message: message.into() }
    }
}

type CommandResult<T> = Result<T, CommandError>;

#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct LinkInfo {
    server_url: String,
}

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
struct StartResponse {
    authorize_url: Option<String>,
    state: Option<String>,
}

#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct CompleteResponse {
    success: bool,
    username: Option<String>,
    country: Option<String>,
}

fn http_client() -> CommandResult<reqwest::Client> {
    reqwest::Client::builder()
        // Local dev runs behind the Aspire gateway's self-signed certificate.
        .danger_accept_invalid_certs(cfg!(debug_assertions))
        .build()
        .map_err(|e| CommandError::new(format!("Could not create HTTP client: {e}")))
}

/// Parses and stores a `nocturne-connect://link?server=…&token=…` link code.
#[tauri::command]
fn link(link_code: String, session: State<'_, SessionState>) -> CommandResult<LinkInfo> {
    let parsed = Url::parse(link_code.trim())
        .map_err(|_| CommandError::new("That doesn't look like a link code. Copy the whole nocturne-connect:// line from Nocturne."))?;

    if parsed.scheme() != "nocturne-connect" {
        return Err(CommandError::new(
            "That doesn't look like a link code. Copy the whole nocturne-connect:// line from Nocturne.",
        ));
    }

    let mut server = None;
    let mut token = None;
    for (key, value) in parsed.query_pairs() {
        match key.as_ref() {
            "server" => server = Some(value.to_string()),
            "token" => token = Some(value.to_string()),
            _ => {}
        }
    }

    let (server, token) = match (server, token) {
        (Some(s), Some(t)) if !s.is_empty() && !t.is_empty() => (s, t),
        _ => return Err(CommandError::new("The link code is missing its server or token part. Generate a fresh one in Nocturne.")),
    };

    let server_url = Url::parse(&server)
        .ok()
        .filter(|u| matches!(u.scheme(), "http" | "https"))
        .ok_or_else(|| CommandError::new("The link code's server address is not a valid URL."))?;

    let server_url = server_url.as_str().trim_end_matches('/').to_string();

    let mut session = session.lock().unwrap();
    session.server_url = Some(server_url.clone());
    session.token = Some(token);
    session.flow_state = None;

    Ok(LinkInfo { server_url })
}

/// Starts the connect flow: asks the server for the Auth0 authorize URL (PKCE verifier stays
/// server-side) and opens it in a dedicated login webview that intercepts the redirect.
///
/// Async on purpose: building a window from a sync command deadlocks on Windows.
#[tauri::command]
async fn start_connect(region: String, app: tauri::AppHandle) -> CommandResult<()> {
    let (server_url, token) = {
        let session = app.state::<SessionState>();
        let session = session.lock().unwrap();
        match (&session.server_url, &session.token) {
            (Some(s), Some(t)) => (s.clone(), t.clone()),
            _ => return Err(CommandError::new("Not linked to a Nocturne server yet.")),
        }
    };

    let response = http_client()?
        .post(format!("{server_url}/api/v4/connectors/carelink/connect/start"))
        .bearer_auth(&token)
        .json(&serde_json::json!({ "server": region }))
        .send()
        .await
        .map_err(|e| CommandError::new(format!("Could not reach {server_url}: {e}")))?;

    let status = response.status().as_u16();
    if !response.status().is_success() {
        return Err(CommandError::http(status, format!("The server rejected the request (HTTP {status}).")));
    }

    let body: StartResponse = response
        .json()
        .await
        .map_err(|e| CommandError::new(format!("Unexpected response from the server: {e}")))?;

    let (authorize_url, flow_state) = match (body.authorize_url, body.state) {
        (Some(a), Some(s)) if !a.is_empty() && !s.is_empty() => (a, s),
        _ => return Err(CommandError::new("The server did not return a sign-in URL.")),
    };

    let authorize_url: Url = authorize_url
        .parse()
        .map_err(|_| CommandError::new("The server returned an invalid sign-in URL."))?;

    {
        let session = app.state::<SessionState>();
        session.lock().unwrap().flow_state = Some(flow_state);
    }

    // Reuse an existing login window (e.g. "start over" while one is open).
    if let Some(existing) = app.get_webview_window(LOGIN_WINDOW_LABEL) {
        let _ = existing.close();
    }

    let handle = app.clone();
    tauri::WebviewWindowBuilder::new(
        &app,
        LOGIN_WINDOW_LABEL,
        tauri::WebviewUrl::External(authorize_url),
    )
    .title("Sign in to CareLink")
    .inner_size(520.0, 760.0)
    .on_navigation(move |url| {
        if url.as_str().starts_with(REDIRECT_SCHEME_PREFIX) {
            if let Some(code) = url
                .query_pairs()
                .find(|(k, _)| k == "code")
                .map(|(_, v)| v.to_string())
            {
                let _ = handle.emit("carelink-code", code);
                if let Some(window) = handle.get_webview_window(LOGIN_WINDOW_LABEL) {
                    let _ = window.close();
                }
            }
            return false; // cancel — the webview can't open the custom scheme anyway
        }
        true
    })
    .build()
    .map_err(|e| CommandError::new(format!("Could not open the sign-in window: {e}")))?;

    Ok(())
}

/// Relays the captured code to the server, which exchanges it (PKCE) and stores the
/// connector secrets for the linked tenant.
#[tauri::command]
async fn complete_connect(code: String, app: tauri::AppHandle) -> CommandResult<CompleteResponse> {
    let (server_url, token, flow_state) = {
        let session = app.state::<SessionState>();
        let session = session.lock().unwrap();
        match (&session.server_url, &session.token, &session.flow_state) {
            (Some(s), Some(t), Some(f)) => (s.clone(), t.clone(), f.clone()),
            _ => return Err(CommandError::new("No sign-in in progress.")),
        }
    };

    let response = http_client()?
        .post(format!("{server_url}/api/v4/connectors/carelink/connect/complete"))
        .bearer_auth(&token)
        .json(&serde_json::json!({ "code": code, "state": flow_state }))
        .send()
        .await
        .map_err(|e| CommandError::new(format!("Could not reach {server_url}: {e}")))?;

    let status = response.status().as_u16();
    if !response.status().is_success() {
        return Err(CommandError::http(status, format!("The server rejected the sign-in (HTTP {status}).")));
    }

    let body: CompleteResponse = response
        .json()
        .await
        .map_err(|e| CommandError::new(format!("Unexpected response from the server: {e}")))?;

    {
        let session = app.state::<SessionState>();
        session.lock().unwrap().flow_state = None;
    }

    Ok(body)
}

/// Closes the login webview if it is open (user cancelled).
#[tauri::command]
fn cancel_login(app: tauri::AppHandle) {
    if let Some(window) = app.get_webview_window(LOGIN_WINDOW_LABEL) {
        let _ = window.close();
    }
}

// ── Glucose companion (durable OAuth + taskbar/widget feed) ─────────────────────
// The CareLink flow above is a one-shot connect. The glucose companion keeps a durable,
// read-scoped token (auth.rs) and writes glucose.json on an interval for the taskbar mod and
// the Windows 11 widget.

// CGM cadence is ~5 min, so polling faster than this buys nothing.
const GLUCOSE_POLL_SECS: u64 = 60;

/// Begins the device-authorization flow for the glucose companion against `server`. Returns the
/// user code + verification URL to display; polling for approval runs in the background and emits
/// `companion-linked` (or `companion-link-failed`) when it resolves.
#[tauri::command]
async fn companion_link_start(
    server: String,
    app: tauri::AppHandle,
) -> CommandResult<auth::DeviceFlowInfo> {
    let client = http_client()?;
    let (info, pending) = auth::begin_device_flow(&client, &server)
        .await
        .map_err(CommandError::new)?;

    tauri::async_runtime::spawn(async move {
        let client = match http_client() {
            Ok(c) => c,
            Err(_) => return,
        };
        match auth::await_authorization(&client, pending).await {
            Ok(()) => {
                let _ = app.emit("companion-linked", ());
                // Populate the taskbar/widget now rather than waiting for the next poll tick.
                poll_glucose(&client, &app).await;
            }
            Err(e) => {
                let _ = app.emit("companion-link-failed", e);
            }
        }
    });

    Ok(info)
}

#[tauri::command]
fn companion_is_linked() -> bool {
    auth::is_linked()
}

#[tauri::command]
fn companion_unlink() -> CommandResult<()> {
    auth::clear().map_err(CommandError::new)
}

/// The latest reading from the last-written glucose file, for the readout to show on load. Live
/// updates arrive via the `glucose-updated` event; this is the initial value.
#[tauri::command]
fn get_current_glucose() -> Option<glucose_poll::CurrentBg> {
    glucose_poll::read_current_from_file()
}

// ── Floating clock ────────────────────────────────────────────────────────────────
// An always-on-top, borderless overlay that hosts the web app's public clock page
// (`{server}/clock/{id}`) so the clock face renders from one place. The window chrome (drag,
// opacity, always-on-top, double-click-to-close) lives in the `float` route; this side just owns
// the window's lifecycle and exposes the linked server URL the route needs to build the clock URL.

/// Creates the floating window (or reveals it if already open) with overlay chrome: no title bar,
/// transparent, always-on-top, off the taskbar. Built async — building a window from a sync
/// command deadlocks on Windows (see `start_connect`).
fn open_floating_window(app: &tauri::AppHandle) -> tauri::Result<()> {
    if let Some(window) = app.get_webview_window(FLOATING_WINDOW_LABEL) {
        window.show()?;
        window.set_focus()?;
        return Ok(());
    }
    tauri::WebviewWindowBuilder::new(
        app,
        FLOATING_WINDOW_LABEL,
        // Trailing slash so this resolves in both dev (Vite serves the `/float/` route) and prod
        // (adapter-static emits `float/index.html`, which Tauri serves for `float/`).
        tauri::WebviewUrl::App("float/".into()),
    )
    .title("Nocturne Glucose")
    .inner_size(220.0, 220.0)
    .min_inner_size(120.0, 120.0)
    .decorations(false)
    .transparent(true)
    .always_on_top(true)
    .skip_taskbar(true)
    // No title bar means no maximize affordance; disabling it also frees double-click on the drag
    // region for the route's close gesture instead of the default maximize toggle.
    .maximizable(false)
    .build()?;
    Ok(())
}

#[tauri::command]
async fn open_floating_clock(app: tauri::AppHandle) -> CommandResult<()> {
    open_floating_window(&app).map_err(|e| CommandError::new(format!("Could not open the floating clock: {e}")))
}

#[tauri::command]
fn close_floating_clock(app: tauri::AppHandle) {
    if let Some(window) = app.get_webview_window(FLOATING_WINDOW_LABEL) {
        let _ = window.close();
    }
}

#[tauri::command]
fn is_floating_clock_open(app: tauri::AppHandle) -> bool {
    app.get_webview_window(FLOATING_WINDOW_LABEL).is_some()
}

/// The linked server's base URL, for the float route to compose `{server}/clock/{id}`. `None` when
/// the companion isn't linked.
#[tauri::command]
fn companion_server_url() -> Option<String> {
    auth::server_url()
}

/// One of the user's clock faces, for the Settings picker. The id is the public-display capability
/// the floating window opens.
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ClockFaceSummary {
    id: String,
    name: String,
}

/// Lists the linked user's clock faces (`GET /api/v4/clockfaces`, bearer-authenticated) so the
/// picker shows their clocks by name instead of asking for a pasted link.
#[tauri::command]
async fn list_clock_faces() -> CommandResult<Vec<ClockFaceSummary>> {
    let client = http_client()?;
    let (server, token) = auth::get_valid_token(&client).await.map_err(CommandError::new)?;
    let resp = client
        .get(format!("{server}/api/v4/clockfaces"))
        .bearer_auth(&token)
        .send()
        .await
        .map_err(|e| CommandError::new(format!("Could not reach {server}: {e}")))?;

    let status = resp.status().as_u16();
    if !resp.status().is_success() {
        return Err(CommandError::http(status, format!("Could not load clocks (HTTP {status}).")));
    }

    resp.json::<Vec<ClockFaceSummary>>()
        .await
        .map_err(|e| CommandError::new(format!("Unexpected response from the server: {e}")))
}

// ── Settings ────────────────────────────────────────────────────────────────────

/// Whether the app is registered to launch at login. Reads the OS-persisted autostart state.
#[tauri::command]
fn get_run_on_startup(app: tauri::AppHandle) -> CommandResult<bool> {
    use tauri_plugin_autostart::ManagerExt;
    app.autolaunch().is_enabled().map_err(|e| CommandError::new(e.to_string()))
}

/// Registers/unregisters launch-at-login. The choice is OS-persisted and survives restarts;
/// the first-run auto-enable in `setup` won't clobber it (see the marker check there).
#[tauri::command]
fn set_run_on_startup(enabled: bool, app: tauri::AppHandle) -> CommandResult<()> {
    use tauri_plugin_autostart::ManagerExt;
    let manager = app.autolaunch();
    let result = if enabled { manager.enable() } else { manager.disable() };
    result.map_err(|e| CommandError::new(e.to_string()))
}

/// One poll cycle: refresh the token, write glucose.json, emit `glucose-updated`. Best-effort —
/// a token-refresh or fetch failure is logged and skipped, never fatal.
async fn poll_glucose(client: &reqwest::Client, app: &tauri::AppHandle) {
    let (server, token) = match auth::get_valid_token(client).await {
        Ok(pair) => pair,
        Err(e) => {
            eprintln!("glucose poller: {e}");
            return;
        }
    };
    match glucose_poll::poll_once(client, &server, &token).await {
        Ok(current) => {
            tray::update_tray(app, current.as_ref());
            let _ = app.emit("glucose-updated", &current);
        }
        Err(e) => eprintln!("glucose poller: {e}"),
    }
}

/// Background poll loop. Polls before sleeping, so a launch (or a just-completed link) populates
/// promptly; idles while unlinked.
fn spawn_glucose_poller(app: tauri::AppHandle) {
    tauri::async_runtime::spawn(async move {
        let client = match http_client() {
            Ok(c) => c,
            Err(e) => {
                eprintln!("glucose poller: could not start: {}", e.message);
                return;
            }
        };
        loop {
            if auth::is_linked() {
                poll_glucose(&client, &app).await;
            }
            tokio::time::sleep(std::time::Duration::from_secs(GLUCOSE_POLL_SECS)).await;
        }
    });
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            None,
        ))
        .manage(SessionState::default())
        .setup(|app| {
            let handle = app.handle();

            // Render `--` until the first poll lands a reading.
            tray::build_tray(handle)?;
            tray::update_tray(handle, None);

            // Launch to the tray at login. Enabled on first run only — afterwards the user's
            // Settings choice (set_run_on_startup) is OS-persisted and must be respected, so a
            // marker file records that the initial opt-in already happened.
            {
                use tauri_plugin_autostart::ManagerExt;
                let marker = app
                    .path()
                    .app_config_dir()
                    .ok()
                    .map(|dir| dir.join("autostart-initialized"));
                let initialized = marker.as_ref().is_some_and(|m| m.exists());
                // Write the marker only after a successful enable, so a failed first-run attempt
                // retries on the next launch instead of being permanently recorded as "done".
                if !initialized && app.autolaunch().enable().is_ok() {
                    if let Some(m) = marker {
                        if let Some(parent) = m.parent() {
                            let _ = std::fs::create_dir_all(parent);
                        }
                        let _ = std::fs::write(&m, b"1");
                    }
                }
            }

            spawn_glucose_poller(handle.clone());
            Ok(())
        })
        .on_window_event(|window, event| {
            // Closing the main window hides it to the tray instead of quitting, so the poller
            // keeps running. Quit is reachable from the tray menu.
            if window.label() == tray::MAIN_WINDOW_LABEL {
                if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                    api.prevent_close();
                    let _ = window.hide();
                }
                return;
            }

            // Tell Settings the floating clock is gone (double-click-to-close, or any other close
            // path) so its toggle flips back off.
            if window.label() == FLOATING_WINDOW_LABEL
                && matches!(event, tauri::WindowEvent::Destroyed)
            {
                let _ = window.app_handle().emit("floating-clock-closed", ());
                return;
            }

            // Let the frontend leave its "waiting for sign-in" state if the user closes the
            // login window. Fires after a capture too; the frontend ignores it then.
            if window.label() == LOGIN_WINDOW_LABEL
                && matches!(event, tauri::WindowEvent::Destroyed)
            {
                let _ = window.app_handle().emit("carelink-login-closed", ());
            }
        })
        .invoke_handler(tauri::generate_handler![
            link,
            start_connect,
            complete_connect,
            cancel_login,
            companion_link_start,
            companion_is_linked,
            companion_unlink,
            get_current_glucose,
            open_floating_clock,
            close_floating_clock,
            is_floating_clock_open,
            companion_server_url,
            list_clock_faces,
            get_run_on_startup,
            set_run_on_startup
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
