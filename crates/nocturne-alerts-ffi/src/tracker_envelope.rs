//! The tracker entry points, documented in the crate `README.md`: the
//! excursion state machine (engine-semantics.md §6) driven without a
//! condition tree, over the same `tracker` state object `evaluate` threads.

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::check_timestamp;
use nocturne_alerts_core::excursion::{
    CloseReason, ExcursionTracker, TrackerRuleConfig, Transition,
};

use crate::envelope::{WireTracker, ok, one, read_request, tracker_json};

/// The tracker is per rule; the calls carry one rule's state, so any fixed
/// key serves.
const RULE: Uuid = Uuid::nil();

#[derive(Deserialize)]
struct WireConfig {
    #[serde(default = "one")]
    confirmation_readings: i32,
    #[serde(default)]
    hysteresis_minutes: i32,
}

impl From<&WireConfig> for TrackerRuleConfig {
    fn from(c: &WireConfig) -> Self {
        TrackerRuleConfig {
            confirmation_readings: c.confirmation_readings,
            hysteresis_minutes: c.hysteresis_minutes,
        }
    }
}

#[derive(Deserialize)]
struct ProcessRequest {
    schema_version: i64,
    /// Absent or null: never evaluated.
    #[serde(default)]
    tracker: Option<WireTracker>,
    config: WireConfig,
    condition_met: bool,
    /// The auto-resolve tree this evaluation; read only while awaiting
    /// re-arm, and absent is false.
    #[serde(default)]
    auto_resolve_met: bool,
    now: DateTime<Utc>,
}

#[derive(Deserialize)]
struct ForceCloseRequest {
    schema_version: i64,
    #[serde(default)]
    tracker: Option<WireTracker>,
    reason: String,
    now: DateTime<Utc>,
}

#[derive(Deserialize)]
struct CloseElapsedRequest {
    schema_version: i64,
    #[serde(default)]
    tracker: Option<WireTracker>,
    config: WireConfig,
    now: DateTime<Utc>,
}

fn restore(wire: Option<&WireTracker>, now: DateTime<Utc>) -> Result<ExcursionTracker, String> {
    check_timestamp(now, "now")?;
    let mut tracker = ExcursionTracker::new();
    if let Some(wire) = wire {
        wire.restore(&mut tracker, RULE)?;
    }
    Ok(tracker)
}

fn respond(transition: Transition, tracker: &ExcursionTracker) -> Value {
    ok(json!({
        "transition": transition_json(transition),
        "tracker": tracker_json(tracker, RULE),
    }))
}

/// `{type, excursion_ordinal?, close_reason?}`; the ordinal names the
/// excursion the transition involved, which a close has already cleared from
/// `tracker`.
fn transition_json(t: Transition) -> Value {
    let mut o = Map::new();
    o.insert("type".into(), t.kind.wire().into());
    if let Some(excursion) = t.excursion {
        o.insert("excursion_ordinal".into(), excursion.into());
    }
    if let Some(reason) = t.close_reason {
        o.insert("close_reason".into(), reason.wire().into());
    }
    Value::Object(o)
}

/// One evaluation's `condition_met` (and, awaiting re-arm, its
/// `auto_resolve_met`) through the state machine.
pub(crate) fn process(request_json: &str) -> Result<Value, String> {
    let req: ProcessRequest = read_request(request_json, |r: &ProcessRequest| r.schema_version)?;
    let mut tracker = restore(req.tracker.as_ref(), req.now)?;
    let transition = tracker.process_evaluation(
        RULE,
        (&req.config).into(),
        req.condition_met,
        req.auto_resolve_met,
        req.now,
    );
    Ok(respond(transition, &tracker))
}

/// Closes the excursion, if there is one, from any state (§6.2).
pub(crate) fn force_close(request_json: &str) -> Result<Value, String> {
    let req: ForceCloseRequest =
        read_request(request_json, |r: &ForceCloseRequest| r.schema_version)?;
    let reason = CloseReason::from_wire(&req.reason)
        .ok_or_else(|| format!("unknown close reason '{}'", req.reason.escape_default()))?;
    let mut tracker = restore(req.tracker.as_ref(), req.now)?;
    let transition = tracker.force_close(RULE, reason, req.now);
    Ok(respond(transition, &tracker))
}

/// Closes an excursion whose hysteresis window has elapsed (§6.1).
pub(crate) fn close_elapsed_hysteresis(request_json: &str) -> Result<Value, String> {
    let req: CloseElapsedRequest =
        read_request(request_json, |r: &CloseElapsedRequest| r.schema_version)?;
    let mut tracker = restore(req.tracker.as_ref(), req.now)?;
    let transition = tracker.close_elapsed_hysteresis(RULE, (&req.config).into(), req.now);
    Ok(respond(transition, &tracker))
}
