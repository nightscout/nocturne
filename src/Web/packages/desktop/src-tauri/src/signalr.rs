//! Minimal SignalR JSON hub-protocol client over a WebSocket.
//!
//! There is no mature Rust SignalR client, so this hand-rolls the parts the companion needs against
//! `DataHub`: HTTP `negotiate` (negotiation is required — the server does not allow skip-negotiation),
//! open the WebSocket with the returned connection token, send the JSON-protocol handshake, invoke the
//! `Authorize` and `Subscribe` hub methods, answer server pings, and surface incoming invocations —
//! the typed device targets plus any target the caller subscribed to by name. Messages are JSON
//! records terminated by the 0x1e record separator.
//!
//! This is deliberately not a general SignalR client: it handles invocation (type 1), ping (type 6),
//! completion (type 3), and close (type 7); other frame types are ignored.

use futures_util::{SinkExt, StreamExt};
use serde::Deserialize;
use std::collections::VecDeque;
use tokio::net::TcpStream;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream};

/// SignalR record separator (RS, 0x1e) that terminates every JSON message.
const RECORD_SEPARATOR: u8 = 0x1e;

/// Collections passed to `Subscribe`: the native V4 categories plus the legacy v1 collections, so
/// realtime fires whether the tenant writes V4 or legacy shapes. Matches the Windows widget.
const COLLECTIONS: [&str; 6] = ["glucose", "care", "device", "entries", "treatments", "devicestatus"];

/// Server events broadcast for the subscribed collections; each means "data changed". Consumers
/// re-fetch over HTTP rather than parsing payloads, so the event name is all a subscriber needs.
pub static DATA_EVENTS: [&str; 5] = ["dataUpdate", "create", "update", "delete", "trackerUpdate"];

type WsStream = WebSocketStream<MaybeTlsStream<TcpStream>>;

/// A connected, authorized DataHub stream that yields device and subscribed data events.
pub struct HubConnection {
    ws: WsStream,
    /// Re-assembly buffer for the 0x1E-framed record stream. The server may split one JSON record
    /// across WS frames or coalesce several into one, so incoming payloads are appended here and only
    /// complete records (up to the last 0x1E) are parsed; the trailing remainder carries forward.
    buffer: String,
    /// Parsed actions drained from the buffer but not yet returned. `recv` returns on the first
    /// actionable record, so the rest of a coalesced batch waits here for the next `recv` instead of
    /// being dropped.
    pending: VecDeque<RecordAction>,
    /// Invocation targets (beyond the typed device targets) the caller wants surfaced as
    /// `HubEvent::Named`; anything else is ignored.
    subscribed: &'static [&'static str],
}

/// A parsed action to take for one complete record, decoupled from the socket so the framing +
/// dispatch logic is unit-testable.
#[derive(Debug)]
enum RecordAction {
    /// Reply to a server ping (type 6) to keep the connection alive.
    Pong,
    /// The server is closing (type 7).
    Close,
    /// A `device_action` invocation (type 1) arrived.
    DeviceAction,
    /// A `device_notification` invocation (type 1) arrived, carrying the parsed mirror.
    DeviceNotification(DeviceNotificationMirror),
    /// A subscribed invocation target arrived. Neither the name nor the payload is carried: the
    /// consumer treats every subscribed event the same ("data changed — re-fetch").
    Named,
    /// Anything else (completions, unsubscribed targets) — no action.
    Ignore,
}

/// The in-app notification mirror the server broadcasts as `device_notification`. The server has
/// already applied the surfacing gate (non-alert, urgency Warn+ or Alert/ActionRequired), so the
/// client just surfaces it — after checking `user_id` matches this device's user. Tolerant of extra
/// fields.
#[derive(Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct DeviceNotificationMirror {
    /// The user the notification belongs to; the client ignores events for other users in the group.
    pub user_id: String,
    pub notification: InAppNotification,
}

/// The minimal in-app notification fields the toast needs (server `InAppNotificationDto` carries
/// more). `id` keys the recently-seen dedup; `title`/`subtitle` render the toast.
#[derive(Deserialize, Clone, Debug, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct InAppNotification {
    pub id: String,
    #[serde(default)]
    pub title: String,
    #[serde(default)]
    pub subtitle: Option<String>,
}

#[derive(Deserialize)]
struct NegotiateResponse {
    #[serde(rename = "connectionToken")]
    connection_token: Option<String>,
    #[serde(rename = "connectionId")]
    connection_id: Option<String>,
    #[serde(rename = "url")]
    redirect_url: Option<String>,
}

/// Result of pumping one batch of incoming frames.
pub enum HubEvent {
    /// A `device_action` invocation arrived. The payload is intentionally dropped: the live event is
    /// only a trigger to reconcile against the active-intents snapshot (the source of truth), so the
    /// caller re-fetches rather than acting on this frame directly.
    DeviceAction,
    /// A `device_notification` (in-app notification mirror) arrived. Unlike device_action this is the
    /// payload itself — there is no snapshot to reconcile against, so the caller toasts it directly
    /// (after the user-id + dedup checks).
    DeviceNotification(DeviceNotificationMirror),
    /// A subscribed data-event invocation arrived (see `DATA_EVENTS`). Like device_action the
    /// payload (and the name — every subscribed event means the same "data changed") is dropped:
    /// the event is only a trigger to re-fetch over HTTP.
    Named,
    /// The server closed the connection (SignalR close frame or socket EOF). Caller should reconnect.
    Closed,
}

impl HubConnection {
    /// Negotiates, opens the WebSocket, performs the JSON handshake, and invokes `Authorize` (with
    /// `token`) and `Subscribe`. Returns a connection ready to `recv` device events plus any
    /// invocation named in `subscribed`. `server` is the tenant base URL (https); the WS URL is
    /// derived from it.
    pub async fn connect(
        server: &str,
        token: &str,
        subscribed: &'static [&'static str],
    ) -> Result<Self, String> {
        let server = server.trim_end_matches('/');
        let connection_token = negotiate(server, token).await?;

        let ws_base = to_ws_scheme(server)?;
        // The access token also gates the HTTP upgrade (tenant + auth come off the handshake).
        let url = format!(
            "{ws_base}/hubs/data?id={}&access_token={}",
            urlencode(&connection_token),
            urlencode(token)
        );

        let (mut ws, _resp) = tokio_tungstenite::connect_async(&url)
            .await
            .map_err(|e| format!("WebSocket connect failed: {e}"))?;

        // JSON-protocol handshake; the server replies with an empty `{}` record on success.
        send_record(&mut ws, br#"{"protocol":"json","version":1}"#).await?;
        read_handshake_ack(&mut ws).await?;

        // Authorize joins the tenant "authorized" group so device_action broadcasts reach us.
        let authorize = serde_json::json!({
            "type": 1,
            "invocationId": "auth-1",
            "target": "Authorize",
            "arguments": [{ "token": token }]
        });
        send_record(&mut ws, authorize.to_string().as_bytes()).await?;

        // Subscribe joins the per-collection groups so the DATA_EVENTS broadcasts reach us. Group
        // membership is per-connection (it does not survive a reconnect), so it must happen here on
        // every new connection, not once per process.
        let subscribe = serde_json::json!({
            "type": 1,
            "invocationId": "sub-1",
            "target": "Subscribe",
            "arguments": [{ "collections": COLLECTIONS }]
        });
        send_record(&mut ws, subscribe.to_string().as_bytes()).await?;

        Ok(Self { ws, buffer: String::new(), pending: VecDeque::new(), subscribed })
    }

    /// Waits for the next hub event, answering pings transparently. Returns `HubEvent::Closed` when
    /// the server closes or the socket ends, so the caller reconnects. The `Authorize` completion is
    /// consumed silently.
    ///
    /// Each WS payload is appended to the re-assembly buffer; only complete 0x1E-terminated records
    /// are dispatched, so a record split across frames is held until its terminator arrives. Records
    /// coalesced into one frame are all parsed onto the pending queue; each `recv` drains the queue
    /// (answering pings) before reading the socket again, so a record behind the one returned is
    /// delivered on the next call rather than lost.
    pub async fn recv(&mut self) -> Result<HubEvent, String> {
        loop {
            while let Some(action) = self.pending.pop_front() {
                match action {
                    RecordAction::Pong => {
                        let _ = send_record(&mut self.ws, br#"{"type":6}"#).await;
                    }
                    RecordAction::Close => return Ok(HubEvent::Closed),
                    RecordAction::DeviceAction => return Ok(HubEvent::DeviceAction),
                    RecordAction::DeviceNotification(mirror) => {
                        return Ok(HubEvent::DeviceNotification(mirror))
                    }
                    RecordAction::Named => return Ok(HubEvent::Named),
                    RecordAction::Ignore => {}
                }
            }

            let msg = match self.ws.next().await {
                Some(Ok(m)) => m,
                Some(Err(e)) => return Err(format!("WebSocket error: {e}")),
                None => return Ok(HubEvent::Closed),
            };

            let payload = match msg {
                Message::Text(t) => t.to_string(),
                Message::Binary(b) => String::from_utf8_lossy(&b).into_owned(),
                Message::Ping(p) => {
                    let _ = self.ws.send(Message::Pong(p)).await;
                    continue;
                }
                Message::Close(_) => return Ok(HubEvent::Closed),
                _ => continue,
            };

            buffer_payload(&mut self.buffer, &mut self.pending, &payload, self.subscribed);
        }
    }
}

/// Appends one WS payload to the re-assembly buffer and parses every newly complete record onto the
/// pending queue in arrival order. Split out of `recv` (no socket involved) so the coalescing
/// behavior is unit-testable.
fn buffer_payload(
    buffer: &mut String,
    pending: &mut VecDeque<RecordAction>,
    payload: &str,
    subscribed: &[&str],
) {
    buffer.push_str(payload);
    for record in drain_complete_records(buffer) {
        pending.push_back(dispatch_record(&record, subscribed));
    }
}

/// Splits every complete (0x1E-terminated) record out of `buffer`, returning them without the
/// separator, and leaves the trailing partial (text after the last 0x1E, possibly empty) in
/// `buffer` to be completed by the next frame. Empty records (e.g. from a leading/duplicate
/// separator) are skipped.
fn drain_complete_records(buffer: &mut String) -> Vec<String> {
    let Some(last_sep) = buffer.rfind(RECORD_SEPARATOR as char) else {
        // No complete record yet; keep accumulating.
        return Vec::new();
    };

    // Everything up to and including the last separator is complete; the rest carries forward.
    let complete = buffer[..last_sep].to_string();
    let remainder = buffer[last_sep + 1..].to_string();
    *buffer = remainder;

    complete
        .split(RECORD_SEPARATOR as char)
        .filter(|r| !r.is_empty())
        .map(str::to_string)
        .collect()
}

/// Classifies one complete record into the action `recv` should take. Type-1 targets outside the
/// typed device pair surface as `Named` when listed in `subscribed`, else are ignored. A genuine
/// parse error on a complete record is logged (it indicates a field/shape mismatch worth
/// diagnosing) and ignored.
fn dispatch_record(record: &str, subscribed: &[&str]) -> RecordAction {
    let value: serde_json::Value = match serde_json::from_str(record) {
        Ok(v) => v,
        Err(e) => {
            eprintln!("signalr: dropping unparseable record: {e}");
            return RecordAction::Ignore;
        }
    };
    let msg_type = value.get("type").and_then(|t| t.as_i64());
    let target = value.get("target").and_then(|t| t.as_str());
    match msg_type {
        // Ping — reply to keep the server's timeout from firing.
        Some(6) => RecordAction::Pong,
        // Close.
        Some(7) => RecordAction::Close,
        // device_action invocation — a trigger to reconcile; the payload is dropped.
        Some(1) if target == Some("device_action") => RecordAction::DeviceAction,
        // device_notification invocation — the mirror IS the payload (arguments[0]), toasted directly.
        Some(1) if target == Some("device_notification") => match parse_first_arg(&value) {
            Some(mirror) => RecordAction::DeviceNotification(mirror),
            None => RecordAction::Ignore,
        },
        // A caller-subscribed data event — arriving is the whole signal; the payload is dropped.
        Some(1) if target.is_some_and(|t| subscribed.iter().any(|s| *s == t)) => RecordAction::Named,
        // Unsubscribed type-1 targets and all other frames (e.g. invocation completions) are ignored.
        _ => RecordAction::Ignore,
    }
}

/// Parses `arguments[0]` of an invocation into a `DeviceNotificationMirror`. Logs and returns `None`
/// on a missing or shape-mismatched argument rather than dropping it silently.
fn parse_first_arg(value: &serde_json::Value) -> Option<DeviceNotificationMirror> {
    let arg = value.get("arguments").and_then(|a| a.as_array()).and_then(|a| a.first())?;
    match serde_json::from_value::<DeviceNotificationMirror>(arg.clone()) {
        Ok(mirror) => Some(mirror),
        Err(e) => {
            eprintln!("signalr: dropping malformed device_notification: {e}");
            None
        }
    }
}

/// `POST {server}/hubs/data/negotiate?negotiateVersion=1` (bearer) → connection token. Honours a
/// one-hop redirect (`url`/`accessToken`) if the server returns one, falling back to connectionId.
async fn negotiate(server: &str, token: &str) -> Result<String, String> {
    let client = crate::http::client()?;

    let resp = client
        .post(format!("{server}/hubs/data/negotiate?negotiateVersion=1"))
        .bearer_auth(token)
        .send()
        .await
        .map_err(|e| format!("negotiate request failed: {e}"))?;

    if !resp.status().is_success() {
        return Err(format!("negotiate returned HTTP {}", resp.status().as_u16()));
    }

    let body: NegotiateResponse = resp
        .json()
        .await
        .map_err(|e| format!("unexpected negotiate response: {e}"))?;

    // A `url` means a redirect to another endpoint; this deployment doesn't use it, so reject rather
    // than silently mis-connect.
    if body.redirect_url.is_some() {
        return Err("negotiate returned a redirect URL, which is unsupported".to_string());
    }

    body.connection_token
        .or(body.connection_id)
        .ok_or_else(|| "negotiate response had no connection token".to_string())
}

/// Sends one JSON record, appending the 0x1e separator.
async fn send_record(ws: &mut WsStream, payload: &[u8]) -> Result<(), String> {
    let mut buf = Vec::with_capacity(payload.len() + 1);
    buf.extend_from_slice(payload);
    buf.push(RECORD_SEPARATOR);
    ws.send(Message::Binary(buf.into()))
        .await
        .map_err(|e| format!("WebSocket send failed: {e}"))
}

/// Reads the first record and treats it as the handshake response: `{}` is success, `{"error":...}`
/// is failure.
async fn read_handshake_ack(ws: &mut WsStream) -> Result<(), String> {
    let msg = ws
        .next()
        .await
        .ok_or("connection closed during handshake")?
        .map_err(|e| format!("handshake read failed: {e}"))?;

    let text = match msg {
        Message::Text(t) => t.to_string(),
        Message::Binary(b) => String::from_utf8_lossy(&b).into_owned(),
        _ => return Err("unexpected handshake frame".to_string()),
    };

    let record = text
        .split(RECORD_SEPARATOR as char)
        .find(|r| !r.is_empty())
        .unwrap_or("{}");

    let value: serde_json::Value =
        serde_json::from_str(record).map_err(|e| format!("bad handshake response: {e}"))?;
    if let Some(err) = value.get("error").and_then(|e| e.as_str()) {
        return Err(format!("handshake rejected: {err}"));
    }
    Ok(())
}

/// Maps an http(s) base URL to its ws(s) equivalent.
fn to_ws_scheme(server: &str) -> Result<String, String> {
    if let Some(rest) = server.strip_prefix("https://") {
        Ok(format!("wss://{rest}"))
    } else if let Some(rest) = server.strip_prefix("http://") {
        Ok(format!("ws://{rest}"))
    } else {
        Err(format!("server URL has no http(s) scheme: {server}"))
    }
}

/// Minimal percent-encoding for query values (token, connection id). Encodes everything outside the
/// unreserved set so base64url/JWT tokens and ids survive intact.
fn urlencode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => out.push(b as char),
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ws_scheme_mapping() {
        assert_eq!(to_ws_scheme("https://t.nocturne.run").unwrap(), "wss://t.nocturne.run");
        assert_eq!(to_ws_scheme("http://localhost:8080").unwrap(), "ws://localhost:8080");
        assert!(to_ws_scheme("t.nocturne.run").is_err());
    }

    #[test]
    fn urlencode_preserves_unreserved_and_escapes_rest() {
        assert_eq!(urlencode("abcABC123-_.~"), "abcABC123-_.~");
        assert_eq!(urlencode("a/b c+d"), "a%2Fb%20c%2Bd");
    }

    const RS: char = RECORD_SEPARATOR as char;

    /// No named subscriptions — the typed device targets don't depend on the set.
    const NO_SUBS: &[&str] = &[];

    #[test]
    fn coalesced_records_in_one_chunk_all_drain() {
        let mut buf = String::new();
        buf.push_str(&format!("{{\"type\":6}}{RS}{{\"type\":1,\"target\":\"device_action\"}}{RS}"));
        let records = drain_complete_records(&mut buf);
        assert_eq!(records.len(), 2);
        assert!(matches!(dispatch_record(&records[0], NO_SUBS), RecordAction::Pong));
        assert!(matches!(dispatch_record(&records[1], NO_SUBS), RecordAction::DeviceAction));
        // Both records were terminated, so nothing carries forward.
        assert!(buf.is_empty());
    }

    #[test]
    fn coalesced_actionable_records_are_all_queued_in_order() {
        // Two actionable records (plus a ping between them) in one coalesced frame: all three are
        // queued, so the ones behind the first returned event are delivered, not dropped, and the
        // mid-batch ping is still answered before the next socket read.
        let mut buf = String::new();
        let mut pending = VecDeque::new();
        let frame = format!(
            "{{\"type\":1,\"target\":\"device_action\"}}{RS}\
             {{\"type\":6}}{RS}\
             {{\"type\":1,\"target\":\"device_notification\",\"arguments\":[\
               {{\"userId\":\"u1\",\"notification\":{{\"id\":\"n1\",\"title\":\"t\"}}}}]}}{RS}"
        );
        buffer_payload(&mut buf, &mut pending, &frame, NO_SUBS);
        assert_eq!(pending.len(), 3);
        assert!(matches!(pending[0], RecordAction::DeviceAction));
        assert!(matches!(pending[1], RecordAction::Pong));
        assert!(matches!(pending[2], RecordAction::DeviceNotification(_)));
        assert!(buf.is_empty());
    }

    #[test]
    fn buffer_payload_holds_partial_record_across_calls() {
        let mut buf = String::new();
        let mut pending = VecDeque::new();
        let full = format!("{{\"type\":1,\"target\":\"device_action\"}}{RS}");
        let split = full.len() / 2;
        buffer_payload(&mut buf, &mut pending, &full[..split], NO_SUBS);
        assert!(pending.is_empty());
        buffer_payload(&mut buf, &mut pending, &full[split..], NO_SUBS);
        assert_eq!(pending.len(), 1);
        assert!(matches!(pending[0], RecordAction::DeviceAction));
    }

    #[test]
    fn record_split_across_two_chunks_reassembles_and_parses_once() {
        let full = format!("{{\"type\":1,\"target\":\"device_action\"}}{RS}");
        let split = full.len() / 2;

        // First chunk has no terminator → nothing drains, the partial is held.
        let mut buf = String::new();
        buf.push_str(&full[..split]);
        assert!(drain_complete_records(&mut buf).is_empty());
        assert_eq!(buf, full[..split]);

        // Second chunk completes the record → it drains exactly once.
        buf.push_str(&full[split..]);
        let records = drain_complete_records(&mut buf);
        assert_eq!(records.len(), 1);
        assert!(matches!(dispatch_record(&records[0], NO_SUBS), RecordAction::DeviceAction));
        assert!(buf.is_empty());
    }

    #[test]
    fn complete_record_then_trailing_partial_carries_remainder_forward() {
        let mut buf = String::new();
        buf.push_str(&format!("{{\"type\":6}}{RS}{{\"type\":1,\"tar"));
        let records = drain_complete_records(&mut buf);
        assert_eq!(records.len(), 1);
        assert!(matches!(dispatch_record(&records[0], NO_SUBS), RecordAction::Pong));
        // The unterminated second record is retained for the next frame.
        assert_eq!(buf, "{\"type\":1,\"tar");
    }

    #[test]
    fn dispatch_classifies_frame_types() {
        assert!(matches!(dispatch_record(r#"{"type":7}"#, NO_SUBS), RecordAction::Close));
        // A type-1 invocation for an unsubscribed target is ignored.
        assert!(matches!(
            dispatch_record(r#"{"type":1,"target":"other"}"#, NO_SUBS),
            RecordAction::Ignore
        ));
        // The Authorize completion (type 3) is ignored.
        assert!(matches!(
            dispatch_record(r#"{"type":3,"invocationId":"auth-1"}"#, NO_SUBS),
            RecordAction::Ignore
        ));
    }

    #[test]
    fn dispatch_surfaces_subscribed_data_events_as_named() {
        // Every data event the glucose path subscribes to must come through.
        for event in DATA_EVENTS {
            let record = format!(r#"{{"type":1,"target":"{event}","arguments":[{{}}]}}"#);
            assert!(
                matches!(dispatch_record(&record, &DATA_EVENTS), RecordAction::Named),
                "expected Named for {event}"
            );
        }
    }

    #[test]
    fn dispatch_ignores_unsubscribed_target_even_with_subscriptions() {
        // An unknown broadcast target must not surface just because a subscription set exists.
        assert!(matches!(
            dispatch_record(r#"{"type":1,"target":"somethingElse"}"#, &DATA_EVENTS),
            RecordAction::Ignore
        ));
        // Device targets stay on their typed variants regardless of the set.
        assert!(matches!(
            dispatch_record(r#"{"type":1,"target":"device_action"}"#, &DATA_EVENTS),
            RecordAction::DeviceAction
        ));
    }

    #[test]
    fn dispatch_parses_device_notification_frame() {
        let frame = r#"{"type":1,"target":"device_notification","arguments":[
            {"userId":"user-1","notification":{"id":"n1","type":"info","category":"System",
             "urgency":"Warn","title":"Sync complete","subtitle":"3 devices updated"}}
        ]}"#;
        match dispatch_record(frame, NO_SUBS) {
            RecordAction::DeviceNotification(m) => {
                assert_eq!(m.user_id, "user-1");
                assert_eq!(m.notification.id, "n1");
                assert_eq!(m.notification.title, "Sync complete");
                assert_eq!(m.notification.subtitle.as_deref(), Some("3 devices updated"));
            }
            other => panic!("expected DeviceNotification, got {other:?}"),
        }
    }

    #[test]
    fn dispatch_ignores_device_notification_without_arguments() {
        // A malformed frame (no arguments) is dropped, not a panic.
        assert!(matches!(
            dispatch_record(r#"{"type":1,"target":"device_notification"}"#, NO_SUBS),
            RecordAction::Ignore
        ));
    }
}
