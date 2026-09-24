//! Request/response envelope: serde wire types, the per-call evaluation
//! driver, and the leaf/path enumerator. The JSON shapes reuse the golden
//! corpus interchange format (`ScenarioRule` / `ScenarioContext` /
//! `ExpectedRuleResult`) verbatim wherever one exists; the state-carrying
//! `timers` / `tracker` objects are defined here and documented in the crate
//! `README.md`.

use std::collections::BTreeMap;

use chrono::{DateTime, SecondsFormat, Utc};
use serde::Deserialize;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use nocturne_alerts_core::classify::classify;
use nocturne_alerts_core::context::{SensorContext, check_timestamp};
use nocturne_alerts_core::engine::{EngineState, Rule, RuleOutcome, evaluate_rule};
use nocturne_alerts_core::eval::{Env, eval_node};
use nocturne_alerts_core::excursion::{
    CloseReason, TrackerState, TrackerStateKind, TransitionType,
};
use nocturne_alerts_core::model::{
    ConditionKind, Container, Node, parse_payload, parse_payload_structure,
};
use nocturne_alerts_core::paths::node_child_path;
use nocturne_alerts_core::sustained::{TimerOp, TimerOpKind, TimerStore};

pub const SCHEMA_VERSION: i64 = 1;

// ---------------------------------------------------------------------------
// Request wire types
// ---------------------------------------------------------------------------

#[derive(Deserialize)]
struct EvaluateRequest {
    schema_version: i64,
    rule: WireRule,
    context: SensorContext,
    now: DateTime<Utc>,
    /// Persisted sustained-timer state for this rule: `path -> first_true`.
    #[serde(default)]
    timers: BTreeMap<String, DateTime<Utc>>,
    /// Persisted tracker state. Absent/null means "never evaluated".
    #[serde(default)]
    tracker: Option<WireTracker>,
}

fn default_confirmation_readings() -> i32 {
    1
}

/// `ScenarioRule` corpus shape (unknown fields such as `name` are ignored).
#[derive(Deserialize)]
struct WireRule {
    id: Uuid,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
    #[serde(default = "default_confirmation_readings")]
    confirmation_readings: i32,
    #[serde(default)]
    hysteresis_minutes: i32,
    #[serde(default)]
    auto_resolve_enabled: bool,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
}

fn default_next_ordinal() -> u32 {
    1
}

#[derive(Deserialize)]
struct WireTracker {
    /// `idle | confirming | active | hysteresis`; absent/null means no
    /// per-rule state exists yet (only the shared ordinal counter is carried).
    #[serde(default)]
    state: Option<String>,
    #[serde(default)]
    confirmation_count: i32,
    #[serde(default)]
    active_excursion_ordinal: Option<u32>,
    /// Required whenever `state` is present.
    #[serde(default)]
    updated_at: Option<DateTime<Utc>>,
    /// When the excursion entered hysteresis. Absent in hysteresis (state
    /// persisted before the field existed) adopts `updated_at` once.
    #[serde(default)]
    hysteresis_started_at: Option<DateTime<Utc>>,
    /// 1-based ordinal the next opened excursion will receive. Shared across
    /// all rules of a tenant/scenario; thread it between calls.
    #[serde(default = "default_next_ordinal")]
    next_excursion_ordinal: u32,
}

// ---------------------------------------------------------------------------
// Evaluate
// ---------------------------------------------------------------------------

pub fn evaluate(request_json: &str) -> Result<Value, String> {
    let req: EvaluateRequest =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    if req.schema_version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported schema_version {} (expected {SCHEMA_VERSION})",
            req.schema_version
        ));
    }

    let kind = ConditionKind::from_wire(&req.rule.condition_type).ok_or_else(|| {
        format!(
            "unknown condition_type '{}'",
            req.rule.condition_type.escape_default()
        )
    })?;

    // A rule body that cannot be evaluated (engine-semantics.md §1.4) skips
    // the rule with its timers and tracker untouched; the host treats an
    // error envelope as that skip.
    if !matches!(req.rule.condition_params, Value::Null)
        && let Err(e) = parse_payload(kind, &req.rule.condition_params)
    {
        return Err(format!(
            "malformed condition_params for '{}': {e}",
            req.rule.condition_type.escape_default()
        ));
    }

    check_timestamp(req.now, "now")?;
    check_timers(&req.timers)?;

    let rule = Rule {
        id: req.rule.id,
        condition_type: kind,
        condition_params: req.rule.condition_params,
        confirmation_readings: req.rule.confirmation_readings,
        hysteresis_minutes: req.rule.hysteresis_minutes,
        auto_resolve_enabled: req.rule.auto_resolve_enabled,
        auto_resolve_params: req.rule.auto_resolve_params,
    };

    let mut state = EngineState::new();
    for (path, at) in &req.timers {
        state.timers.seed(rule.id, path, *at);
    }
    if let Some(w) = &req.tracker {
        state
            .tracker
            .set_next_excursion_ordinal(w.next_excursion_ordinal);
        if let Some(s) = &w.state {
            let state_kind = TrackerStateKind::from_wire(s)
                .ok_or_else(|| format!("unknown tracker state '{}'", s.escape_default()))?;
            let updated_at = w
                .updated_at
                .ok_or("tracker.updated_at is required when tracker.state is present")?;
            check_timestamp(updated_at, "tracker.updated_at")?;
            if let Some(at) = w.hysteresis_started_at {
                check_timestamp(at, "tracker.hysteresis_started_at")?;
            }
            state.tracker.restore_state(
                rule.id,
                TrackerState {
                    state: state_kind,
                    confirmation_count: w.confirmation_count,
                    active_excursion: w.active_excursion_ordinal,
                    updated_at,
                    hysteresis_started_at: w.hysteresis_started_at,
                },
            );
        }
    }

    let outcome = evaluate_rule(&rule, &req.context, req.now, &mut state);

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "result": outcome_json(&outcome),
        "timers": timers_json(&state, rule.id),
        "tracker": tracker_state_json(&state, rule.id),
    }))
}

fn check_timers(timers: &BTreeMap<String, DateTime<Utc>>) -> Result<(), String> {
    timers
        .values()
        .try_for_each(|at| check_timestamp(*at, "timers").map(|_| ()))
}

/// RFC 3339 UTC; whole seconds render without a fraction (matching the corpus
/// `yyyy-MM-ddTHH:mm:ssZ` form), sub-second instants keep their precision.
fn fmt_at(at: DateTime<Utc>) -> String {
    at.to_rfc3339_opts(SecondsFormat::AutoSi, true)
}

fn timer_op_json(op: &TimerOp) -> Value {
    let mut o = Map::new();
    o.insert(
        "op".into(),
        Value::String(
            match op.kind {
                TimerOpKind::Set => "set",
                TimerOpKind::Clear => "clear",
            }
            .into(),
        ),
    );
    o.insert("path".into(), Value::String(op.path.clone()));
    if let Some(at) = op.at {
        o.insert("at".into(), Value::String(fmt_at(at)));
    }
    Value::Object(o)
}

/// `ExpectedRuleResult` corpus shape (mirrors the parity harness exactly).
fn outcome_json(outcome: &RuleOutcome) -> Value {
    let mut o = Map::new();
    o.insert("rule_id".into(), Value::String(outcome.rule_id.to_string()));
    if outcome.skipped {
        o.insert("skipped".into(), Value::Bool(true));
        return Value::Object(o);
    }
    o.insert("root".into(), Value::Bool(outcome.root.expect("root set")));
    o.insert(
        "leaves".into(),
        Value::Array(
            outcome
                .leaves
                .iter()
                .map(|(leaf_id, value)| json!({ "leaf_id": leaf_id, "value": value }))
                .collect(),
        ),
    );
    let transition = outcome.transition.expect("transition set");
    o.insert(
        "transition".into(),
        Value::String(
            match transition.kind {
                TransitionType::None => "none",
                TransitionType::ExcursionOpened => "opened",
                TransitionType::ExcursionContinues => "continues",
                TransitionType::HysteresisStarted => "hysteresis_started",
                TransitionType::HysteresisResumed => "hysteresis_resumed",
                TransitionType::ExcursionClosed => "closed",
            }
            .into(),
        ),
    );
    if let Some(reason) = transition.close_reason {
        o.insert(
            "close_reason".into(),
            Value::String(
                match reason {
                    CloseReason::Hysteresis => "hysteresis",
                    CloseReason::AutoResolve => "auto",
                    CloseReason::Manual => "manual",
                }
                .into(),
            ),
        );
    }
    if let Some(tracker) = &outcome.tracker {
        let mut t = Map::new();
        t.insert("state".into(), Value::String(tracker.state.wire().into()));
        t.insert(
            "confirmation_count".into(),
            Value::Number(tracker.confirmation_count.into()),
        );
        if let Some(excursion) = tracker.excursion {
            t.insert("excursion".into(), Value::Number(excursion.into()));
        }
        if let Some(at) = tracker.hysteresis_started_at {
            t.insert("hysteresis_started_at".into(), Value::String(fmt_at(at)));
        }
        o.insert("tracker".into(), Value::Object(t));
    }
    if outcome.auto_resolved {
        o.insert("auto_resolved".into(), Value::Bool(true));
    }
    if !outcome.timer_ops.is_empty() {
        o.insert(
            "timer_ops".into(),
            Value::Array(outcome.timer_ops.iter().map(timer_op_json).collect()),
        );
    }
    Value::Object(o)
}

/// Post-evaluation timer state for the rule: `path -> first_true`.
fn timers_json(state: &EngineState, rule_id: Uuid) -> Value {
    let mut o = Map::new();
    for (path, at) in state.timers.snapshot_for_rule(rule_id) {
        o.insert(path, Value::String(fmt_at(at)));
    }
    Value::Object(o)
}

/// Post-evaluation tracker state. `state`/`confirmation_count`/`updated_at`
/// (plus `active_excursion_ordinal` when an excursion is active and
/// `hysteresis_started_at` while in hysteresis) are present
/// only once per-rule state exists; `next_excursion_ordinal` is always
/// present and must be threaded into the next call (shared across rules).
fn tracker_state_json(state: &EngineState, rule_id: Uuid) -> Value {
    let mut t = Map::new();
    if let Some(s) = state.tracker.state(rule_id) {
        t.insert("state".into(), Value::String(s.state.wire().into()));
        t.insert(
            "confirmation_count".into(),
            Value::Number(s.confirmation_count.into()),
        );
        if let Some(excursion) = s.active_excursion {
            t.insert(
                "active_excursion_ordinal".into(),
                Value::Number(excursion.into()),
            );
        }
        t.insert("updated_at".into(), Value::String(fmt_at(s.updated_at)));
        if let Some(at) = s.hysteresis_started_at {
            t.insert("hysteresis_started_at".into(), Value::String(fmt_at(at)));
        }
    }
    t.insert(
        "next_excursion_ordinal".into(),
        Value::Number(state.tracker.next_excursion_ordinal().into()),
    );
    Value::Object(t)
}

// ---------------------------------------------------------------------------
// Evaluate node
// ---------------------------------------------------------------------------

/// Request for `nocturne_alerts_evaluate_node`: a single condition tree
/// evaluated outside the per-rule driver (no tracker, no auto-resolve). Used
/// by hosts for auxiliary evaluation scopes — smart-snooze conditions
/// (`root: "snooze"`) and the sweep's periodic auto-resolve
/// (`root: "auto_resolve"`) — which in C# go through
/// `ConditionEvaluatorRegistry.EvaluateNodeAsync` with a reserved path root.
#[derive(Deserialize)]
struct EvaluateNodeRequest {
    schema_version: i64,
    /// Keys sustained timers, exactly like the rule id in `evaluate`.
    rule_id: Uuid,
    /// A full ConditionNode object (`{"type": …, …}`).
    node: Value,
    /// Root path segment override (e.g. `"snooze"`, `"auto_resolve"`).
    /// Defaults to the node's verbatim `type` string.
    #[serde(default)]
    root: Option<String>,
    context: SensorContext,
    now: DateTime<Utc>,
    /// Persisted sustained-timer state for the rule: `path -> first_true`.
    #[serde(default)]
    timers: BTreeMap<String, DateTime<Utc>>,
}

/// Evaluates one condition node for one instant. A node that cannot be
/// evaluated (engine-semantics.md §1.4) is an envelope error; the C# callers
/// treat the matching exception as `false`.
pub fn evaluate_node_envelope(request_json: &str) -> Result<Value, String> {
    let req: EvaluateNodeRequest =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    if req.schema_version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported schema_version {} (expected {SCHEMA_VERSION})",
            req.schema_version
        ));
    }

    check_timestamp(req.now, "now")?;
    check_timers(&req.timers)?;

    let node = match &req.root {
        Some(root) => Node::parse_rooted(&req.node, root),
        None => Node::parse(&req.node),
    }
    .map_err(|e| format!("malformed condition node: {e}"))?;
    let root = req
        .root
        .unwrap_or_else(|| node.type_str.clone().unwrap_or_default());

    let mut timers = TimerStore::new();
    for (path, at) in &req.timers {
        timers.seed(req.rule_id, path, *at);
    }

    let value = {
        let mut env = Env::new(req.now, req.rule_id, &req.context, &mut timers);
        eval_node(Some(&node), &root, &mut env)
    };

    let ops = timers.drain_ops();
    let mut timers_obj = Map::new();
    for (path, at) in timers.snapshot_for_rule(req.rule_id) {
        timers_obj.insert(path, Value::String(fmt_at(at)));
    }

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "value": value,
        "timers": Value::Object(timers_obj),
        "timer_ops": ops.iter().map(timer_op_json).collect::<Vec<_>>(),
    }))
}

// ---------------------------------------------------------------------------
// Classify
// ---------------------------------------------------------------------------

/// Request for `nocturne_alerts_classify`: a rule's root `condition_type` plus
/// its payload-only `condition_params` (exactly as stored in
/// `alert_rules.condition_params`). Mirrors `WireRule`'s discriminator + payload
/// pair, without the per-rule driver fields classification never reads.
#[derive(Deserialize)]
struct ClassifyRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
}

/// Derives a rule's scope class (`low | high | composite | undirected`) for
/// scoped Do Not Disturb (ADR 0004). Unlike `evaluate`, an unknown
/// `condition_type` or malformed `condition_params` is **not** an envelope
/// error: the crate's `classify` silent-fails to `undirected` (all-only), the
/// safe default that never lets a scoped mute silence an unclassifiable rule.
/// Only a structurally malformed *envelope* is an error.
pub fn classify_envelope(request_json: &str) -> Result<Value, String> {
    let req: ClassifyRequest =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    if req.schema_version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported schema_version {} (expected {SCHEMA_VERSION})",
            req.schema_version
        ));
    }

    let class = classify(&req.condition_type, &req.condition_params);

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "scope_class": class.wire(),
    }))
}

// ---------------------------------------------------------------------------
// Leaf paths
// ---------------------------------------------------------------------------

/// Input: a full ConditionNode object (`{"type": …, …}`), or a wrapper
/// `{"node": {…}, "root": "auto_resolve"}` overriding the root path segment
/// (defaults to the node's verbatim `type` string, mirroring
/// `ConditionPath.Walk`).
pub fn leaf_paths(input_json: &str) -> Result<Value, String> {
    let v: Value = serde_json::from_str(input_json).map_err(|e| format!("invalid JSON: {e}"))?;

    let (node_value, root_override) = match &v {
        Value::Object(o) if o.contains_key("node") => {
            let root = match o.get("root") {
                None | Some(Value::Null) => None,
                Some(Value::String(s)) => Some(s.clone()),
                Some(_) => return Err("'root' must be a string".into()),
            };
            (o.get("node").expect("checked above"), root)
        }
        Value::Object(_) => (&v, None),
        _ => return Err("condition node must be a JSON object".into()),
    };

    let node =
        Node::parse_structure(node_value).map_err(|e| format!("malformed condition node: {e}"))?;
    let root = root_override.unwrap_or_else(|| node.type_str.clone().unwrap_or_default());

    let mut paths = Vec::new();
    let mut leaves = Vec::new();
    walk(Some(&node), root.clone(), &mut paths, &mut leaves);

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "root": root,
        "paths": paths,
        "leaves": leaves
            .iter()
            .enumerate()
            .map(|(leaf_id, path)| json!({ "leaf_id": leaf_id, "path": path }))
            .collect::<Vec<_>>(),
    }))
}

/// Pre-order walk emitting every node slot's canonical path; leaves are
/// assigned ids as `collect_leaves` does.
fn walk(node: Option<&Node>, path: String, paths: &mut Vec<String>, leaves: &mut Vec<String>) {
    paths.push(path.clone());
    match node.and_then(Node::container) {
        Some(container) => {
            for (i, child) in container.children().enumerate() {
                walk(child, node_child_path(&path, i, child), paths, leaves);
            }
        }
        None => leaves.push(path),
    }
}

// ---------------------------------------------------------------------------
// Describe (ADR 0007 — condition readouts)
// ---------------------------------------------------------------------------

/// Request for `nocturne_alerts_describe`: a rule's stored `condition_type` +
/// `condition_params` (the same split shape `classify` takes). Static — no
/// `SensorContext`, no `now`.
#[derive(Deserialize)]
struct DescribeRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
}

/// Decodes a rule's opaque condition tree into a structured, leaf-id-tagged
/// description for host-rendered condition readouts (Prelude, ADR 0007).
///
/// Leaf ids are assigned by the **same** pre-order walk the engine uses for its
/// force-eval log (`collect_leaves` over `Node::from_rule`), so a host joins
/// this static description to each tick's `result.leaves[]` by `leaf_id`. The
/// description carries only authored operands (thresholds, durations,
/// operators) and tree structure — never truth or observed values, which the
/// host pairs in from `evaluate` and its own `SensorContext`.
pub fn describe(request_json: &str) -> Result<Value, String> {
    let req: DescribeRequest =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    if req.schema_version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported schema_version {} (expected {SCHEMA_VERSION})",
            req.schema_version
        ));
    }

    let kind = ConditionKind::from_wire(&req.condition_type).ok_or_else(|| {
        format!(
            "unknown condition_type '{}'",
            req.condition_type.escape_default()
        )
    })?;

    // Reconstitute the node as the engine's leaf log does (engine.rs) so leaf
    // ids align with `result.leaves[]`. The structural parse alone: a rule the
    // engine skips still describes as authored, and a malformed payload
    // collapses to a single leaf.
    let payload = match &req.condition_params {
        Value::Null => None,
        v => parse_payload_structure(kind, v).ok(),
    };
    let full_node = Node::from_rule(kind, payload);

    // Root path is the kind's wire name — the same root the engine keys timers
    // and leaf paths under, so a host joins a sustained node's `path` straight
    // to its persisted timer (`condition_timers.path`).
    let root_path = kind.wire().to_string();
    let mut next_leaf_id = 0;
    let tree = describe_node(Some(&full_node), root_path, &mut next_leaf_id);

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "tree": tree,
    }))
}

/// Pre-order walk assigning leaf ids as `collect_leaves` does. A leaf
/// describes the payload evaluation reads, so a kind reached through its
/// member name or ordinal shows its defaults, not the operands it ignores.
fn describe_node(node: Option<&Node>, path: String, next_leaf_id: &mut usize) -> Value {
    if let Some(container) = node.and_then(Node::container) {
        let mut children = container
            .children()
            .enumerate()
            .map(|(i, child)| describe_node(child, node_child_path(&path, i, child), next_leaf_id));
        return match container {
            Container::Composite(p, _) => json!({
                "type": "composite",
                "path": path,
                "operator": p.operator,
                "conditions": children.collect::<Vec<_>>(),
            }),
            Container::Not(_) => json!({ "type": "not", "path": path, "child": children.next() }),
            Container::Sustained(p, _) => json!({
                "type": "sustained",
                "path": path,
                "minutes": p.minutes,
                "child": children.next(),
            }),
        };
    }

    let leaf_id = *next_leaf_id;
    *next_leaf_id += 1;
    let payload = node.and_then(Node::dispatch);
    json!({
        "leaf_id": leaf_id,
        "path": path,
        "type": node.and_then(|n| n.type_str.as_deref()),
        "kind": payload.as_ref().map(|p| p.kind().wire()),
        "params": payload,
    })
}
