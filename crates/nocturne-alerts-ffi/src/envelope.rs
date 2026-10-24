//! The request and response envelopes documented in the crate `README.md`.
//! Rules, contexts and results use the golden corpus interchange shapes; the
//! `timers` and `tracker` objects carry evaluation state between calls.

use std::collections::BTreeMap;

use chrono::{DateTime, Utc};
use serde::Deserialize;
use serde::de::DeserializeOwned;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use nocturne_alerts_core::classify::classify;
use nocturne_alerts_core::context::{SensorContext, check_timestamp};
use nocturne_alerts_core::engine::{
    EngineState, Rule, WireRule, evaluate_parsed_rule, format_instant, parse_auto_resolve,
};
use nocturne_alerts_core::enums::WireEnum;
use nocturne_alerts_core::eval::{Env, eval_node};
use nocturne_alerts_core::excursion::{ExcursionTracker, TrackerState, TrackerStateKind};
use nocturne_alerts_core::facts::{self, ContextFact, RuleConditions};
use nocturne_alerts_core::model::{ConditionKind, Container, Node, parse_payload};
use nocturne_alerts_core::paths::{AUTO_RESOLVE_ROOT, node_child_path};
use nocturne_alerts_core::sustained::{TimerOp, TimerStore};
use nocturne_alerts_core::wall_clock::references_wall_clock;

pub(crate) const SCHEMA_VERSION: i64 = 1;

fn known_kind(condition_type: &str) -> Result<ConditionKind, String> {
    ConditionKind::from_wire(condition_type).ok_or_else(|| {
        format!(
            "unknown condition_type '{}'",
            condition_type.escape_default()
        )
    })
}

/// Reads a request envelope and checks its `schema_version`.
pub(crate) fn read_request<T: DeserializeOwned>(
    request_json: &str,
    schema_version: impl FnOnce(&T) -> i64,
) -> Result<T, String> {
    let req: T =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    match schema_version(&req) {
        SCHEMA_VERSION => Ok(req),
        other => Err(format!(
            "unsupported schema_version {other} (expected {SCHEMA_VERSION})"
        )),
    }
}

/// The `ok: true` envelope around `fields`.
pub(crate) fn ok(fields: Value) -> Value {
    let mut o = Map::new();
    o.insert("schema_version".into(), SCHEMA_VERSION.into());
    o.insert("ok".into(), true.into());
    if let Value::Object(fields) = fields {
        o.extend(fields);
    }
    Value::Object(o)
}

/// Persisted sustained-timer state for one rule: `path -> first_true`.
type WireTimers = BTreeMap<String, DateTime<Utc>>;

/// Checks and loads persisted timers.
fn seed_timers(timers: &mut TimerStore, rule_id: Uuid, wire: &WireTimers) -> Result<(), String> {
    for (path, &at) in wire {
        timers.seed(rule_id, path, check_timestamp(at, "timers")?);
    }
    Ok(())
}

/// A rule's timers after evaluation: `path -> first_true`.
fn timers_json(timers: &TimerStore, rule_id: Uuid) -> Value {
    timers
        .snapshot_for_rule(rule_id)
        .map(|(path, at)| (path.to_owned(), format_instant(at).into()))
        .collect::<Map<_, _>>()
        .into()
}

#[derive(Deserialize)]
struct EvaluateRequest {
    schema_version: i64,
    rule: WireRule,
    context: SensorContext,
    now: DateTime<Utc>,
    #[serde(default)]
    timers: WireTimers,
    /// Absent or null: never evaluated.
    #[serde(default)]
    tracker: Option<WireTracker>,
    #[serde(default = "yes")]
    include_leaves: bool,
}

fn yes() -> bool {
    true
}

pub(crate) fn one<T: From<u8>>() -> T {
    T::from(1)
}

#[derive(Deserialize)]
pub(crate) struct WireTracker {
    /// Absent or null: no per-rule state yet, only the shared ordinal.
    #[serde(default)]
    state: Option<String>,
    #[serde(default)]
    confirmation_count: i32,
    #[serde(default)]
    active_excursion_ordinal: Option<u32>,
    /// Required whenever `state` is present.
    #[serde(default)]
    updated_at: Option<DateTime<Utc>>,
    /// Absent in hysteresis, `updated_at` is adopted once.
    #[serde(default)]
    hysteresis_started_at: Option<DateTime<Utc>>,
    #[serde(default)]
    awaiting_rearm: bool,
    #[serde(default = "one")]
    next_excursion_ordinal: u32,
}

impl WireTracker {
    pub(crate) fn restore(
        &self,
        tracker: &mut ExcursionTracker,
        rule_id: Uuid,
    ) -> Result<(), String> {
        tracker.set_next_excursion_ordinal(self.next_excursion_ordinal);
        let Some(s) = &self.state else {
            return Ok(());
        };
        let updated_at = self
            .updated_at
            .ok_or("tracker.updated_at is required when tracker.state is present")?;
        check_timestamp(updated_at, "tracker.updated_at")?;
        if let Some(at) = self.hysteresis_started_at {
            check_timestamp(at, "tracker.hysteresis_started_at")?;
        }
        let restored = match TrackerStateKind::from_wire(s) {
            Some(state) => TrackerState {
                state,
                confirmation_count: self.confirmation_count,
                active_excursion: self.active_excursion_ordinal,
                updated_at,
                hysteresis_started_at: self.hysteresis_started_at,
                awaiting_rearm: self.awaiting_rearm,
            },
            None => TrackerState::recovered(self.active_excursion_ordinal, updated_at),
        };
        tracker.restore_state(rule_id, restored);
        Ok(())
    }
}

pub(crate) fn evaluate(request_json: &str) -> Result<Value, String> {
    let req: EvaluateRequest = read_request(request_json, |r: &EvaluateRequest| r.schema_version)?;
    let rule = Rule::try_from(req.rule)?;

    // A body that cannot be evaluated (engine-semantics.md §1.4) is an error,
    // which the host treats as skipping the rule with its state untouched.
    let body = rule.parse_body().map_err(|e| {
        format!(
            "malformed condition_params for '{}': {e}",
            rule.condition_type.name()
        )
    })?;
    check_timestamp(req.now, "now")?;

    let mut state = EngineState::new();
    seed_timers(&mut state.timers, rule.id, &req.timers)?;
    if let Some(tracker) = &req.tracker {
        tracker.restore(&mut state.tracker, rule.id)?;
    }

    let outcome = evaluate_parsed_rule(
        &rule,
        &body,
        &req.context,
        req.now,
        &mut state,
        req.include_leaves,
    );
    Ok(ok(json!({
        "result": outcome.to_json(),
        "timers": timers_json(&state.timers, rule.id),
        "tracker": tracker_json(&state.tracker, rule.id),
    })))
}

/// The tracker after evaluation. The per-rule fields are present once the
/// rule has state; `next_excursion_ordinal` always is.
pub(crate) fn tracker_json(tracker: &ExcursionTracker, rule_id: Uuid) -> Value {
    let mut t = Map::new();
    if let Some(s) = tracker.state(rule_id) {
        t.insert("state".into(), s.state.wire().into());
        t.insert("confirmation_count".into(), s.confirmation_count.into());
        if let Some(excursion) = s.active_excursion {
            t.insert("active_excursion_ordinal".into(), excursion.into());
        }
        t.insert("updated_at".into(), format_instant(s.updated_at).into());
        if let Some(at) = s.hysteresis_started_at {
            t.insert("hysteresis_started_at".into(), format_instant(at).into());
        }
        if s.awaiting_rearm {
            t.insert("awaiting_rearm".into(), true.into());
        }
    }
    t.insert(
        "next_excursion_ordinal".into(),
        tracker.next_excursion_ordinal().into(),
    );
    Value::Object(t)
}

/// One condition tree for one instant, outside the per-rule driver: no
/// tracker, no auto-resolve, no leaf log.
#[derive(Deserialize)]
struct EvaluateNodeRequest {
    schema_version: i64,
    /// Keys sustained timers, as the rule id does in `evaluate`.
    rule_id: Uuid,
    node: Value,
    /// The root path segment; defaults to the node's `type` as written.
    #[serde(default)]
    root: Option<String>,
    context: SensorContext,
    now: DateTime<Utc>,
    #[serde(default)]
    timers: WireTimers,
}

/// A node that cannot be evaluated (engine-semantics.md §1.4) is an error.
pub(crate) fn evaluate_node(request_json: &str) -> Result<Value, String> {
    let req: EvaluateNodeRequest =
        read_request(request_json, |r: &EvaluateNodeRequest| r.schema_version)?;
    check_timestamp(req.now, "now")?;
    let mut timers = TimerStore::new();
    seed_timers(&mut timers, req.rule_id, &req.timers)?;

    let node = match &req.root {
        Some(root) => Node::parse_rooted(&req.node, root),
        None => Node::parse(&req.node),
    }
    .map_err(|e| format!("malformed condition node: {e}"))?;
    let root = req.root.unwrap_or_else(|| node.kind().name().to_owned());

    let mut env = Env::new(req.now, req.rule_id, &req.context, &mut timers);
    let value = eval_node(&node, &root, &mut env);
    let ops = timers.drain_ops();
    Ok(ok(json!({
        "value": value,
        "timers": timers_json(&timers, req.rule_id),
        "timer_ops": ops.iter().map(TimerOp::to_json).collect::<Vec<_>>(),
    })))
}

/// A rule body as stored: its root `condition_type` and payload-only
/// `condition_params`.
#[derive(Deserialize)]
struct RuleBodyRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
}

fn read_rule_body(request_json: &str) -> Result<RuleBodyRequest, String> {
    read_request(request_json, |r: &RuleBodyRequest| r.schema_version)
}

/// A rule's scope class for scoped Do Not Disturb (ADR 0004). An unknown
/// type or unevaluable body is `undirected`, not an error.
pub(crate) fn classify_rule(request_json: &str) -> Result<Value, String> {
    let req = read_rule_body(request_json)?;
    let class = classify(&req.condition_type, &req.condition_params);
    Ok(ok(json!({ "scope_class": class.wire() })))
}

/// A stored rule's condition trees: the body as for [`RuleBodyRequest`],
/// plus the auto-resolve configuration as a rule carries it.
#[derive(Deserialize)]
struct RuleConditionsRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
    #[serde(default)]
    auto_resolve_enabled: bool,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
}

impl RuleConditionsRequest {
    fn read(request_json: &str) -> Result<Self, String> {
        read_request(request_json, |r: &Self| r.schema_version)
    }

    fn rule(&self) -> RuleConditions<'_> {
        RuleConditions {
            condition_type: &self.condition_type,
            condition_params: &self.condition_params,
            auto_resolve_enabled: self.auto_resolve_enabled,
            auto_resolve_params: self.auto_resolve_params.as_ref(),
        }
    }
}

/// Whether a rule must also be evaluated on a timer, not only per reading
/// (engine-semantics.md §5.1). An unknown type or unevaluable body is
/// `false`, not an error.
pub(crate) fn wall_clock(request_json: &str) -> Result<Value, String> {
    let req = RuleConditionsRequest::read(request_json)?;
    let wall_clock = references_wall_clock(&req.rule());
    Ok(ok(json!({ "references_wall_clock": wall_clock })))
}

/// The context facts a rule reads (engine-semantics.md §5.2), by wire name.
/// An unknown type or unevaluable body reads none, not an error.
pub(crate) fn rule_reads(request_json: &str) -> Result<Value, String> {
    let req = RuleConditionsRequest::read(request_json)?;
    let names: Vec<&str> = facts::rule_reads(&req.rule())
        .into_iter()
        .map(ContextFact::name)
        .collect();
    Ok(ok(json!({ "reads": names })))
}

/// [`RuleConditionsRequest`] plus the fact names to match.
#[derive(Deserialize)]
struct ReferencesAnyRequest {
    #[serde(flatten)]
    rule: RuleConditionsRequest,
    facts: Vec<String>,
}

/// Whether a rule reads any of the request's `facts`. A fact name this crate
/// does not know is an error: ignoring it could skip a rule the host meant to
/// evaluate.
pub(crate) fn references_any(request_json: &str) -> Result<Value, String> {
    let req: ReferencesAnyRequest = read_request(request_json, |r: &ReferencesAnyRequest| {
        r.rule.schema_version
    })?;
    let facts = req
        .facts
        .iter()
        .map(|name| {
            ContextFact::from_name(name)
                .ok_or_else(|| format!("unknown fact '{}'", name.escape_default()))
        })
        .collect::<Result<Vec<_>, String>>()?;
    let matched = facts::references_any(&req.rule.rule(), &facts);
    Ok(ok(json!({ "references_any": matched })))
}

/// Every node's condition path and the leaves' paths by leaf id. Input is a
/// condition node, or `{"node": …, "root": …}` naming the root segment, which
/// defaults to the node's `type`. A node that cannot be evaluated is an error.
pub(crate) fn leaf_paths(input_json: &str) -> Result<Value, String> {
    let v: Value = serde_json::from_str(input_json).map_err(|e| format!("invalid JSON: {e}"))?;
    let (node_value, root_override) = match &v {
        Value::Object(o) => match o.get("node") {
            Some(node) => match o.get("root") {
                None | Some(Value::Null) => (node, None),
                Some(Value::String(s)) => (node, Some(s.clone())),
                Some(_) => return Err("'root' must be a string".into()),
            },
            None => (&v, None),
        },
        _ => return Err("condition node must be a JSON object".into()),
    };

    let node = match &root_override {
        Some(root) => Node::parse_rooted(node_value, root),
        None => Node::parse(node_value),
    }
    .map_err(|e| format!("malformed condition node: {e}"))?;
    let root = root_override.unwrap_or_else(|| node.kind().name().to_owned());
    let mut paths = Vec::new();
    let mut leaves = Vec::new();
    walk_paths(&node, root.clone(), &mut paths, &mut leaves);
    let leaves = leaves.iter().enumerate();
    Ok(ok(json!({
        "root": root,
        "paths": paths,
        "leaves": leaves
            .map(|(leaf_id, path)| json!({ "leaf_id": leaf_id, "path": path }))
            .collect::<Vec<_>>(),
    })))
}

/// Pre-order; leaves are pushed in leaf-id order.
fn walk_paths(node: &Node, path: String, paths: &mut Vec<String>, leaves: &mut Vec<String>) {
    paths.push(path.clone());
    match node.container() {
        Some(container) => {
            for (i, child) in container.children().enumerate() {
                walk_paths(child, node_child_path(&path, i, child), paths, leaves);
            }
        }
        None => leaves.push(path),
    }
}

/// [`RuleBodyRequest`] plus, optionally, an auto-resolve tree to describe.
#[derive(Deserialize)]
struct DescribeRequest {
    #[serde(flatten)]
    body: RuleBodyRequest,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
}

/// A rule's condition tree as leaf-id-tagged authored operands, for condition
/// readouts (ADR 0007). Leaf ids and paths match `evaluate`'s leaf log and
/// timer keys. `tree` is `null` for a body that cannot be evaluated. A request
/// carrying `auto_resolve_params` also gets
/// `auto_resolve_tree`, under the `auto_resolve` path root, or `null` when that
/// tree is `null` or cannot be evaluated.
pub(crate) fn describe(request_json: &str) -> Result<Value, String> {
    let req: DescribeRequest =
        read_request(request_json, |r: &DescribeRequest| r.body.schema_version)?;
    let kind = known_kind(&req.body.condition_type)?;
    let tree = parse_payload(kind, &req.body.condition_params).map_or(Value::Null, |payload| {
        describe_node(&Node::from(payload), kind.name().to_owned(), &mut 0)
    });
    let mut response = Map::new();
    response.insert("tree".to_owned(), tree);
    if let Some(params) = &req.auto_resolve_params {
        let auto_resolve = parse_auto_resolve(Some(params)).map_or(Value::Null, |tree| {
            describe_node(&tree, AUTO_RESOLVE_ROOT.to_owned(), &mut 0)
        });
        response.insert("auto_resolve_tree".to_owned(), auto_resolve);
    }
    Ok(ok(Value::Object(response)))
}

/// Pre-order, numbering leaves as the leaf log does.
fn describe_node(node: &Node, path: String, next_leaf_id: &mut usize) -> Value {
    if let Some(container) = node.container() {
        let mut children = container
            .children()
            .enumerate()
            .map(|(i, child)| describe_node(child, node_child_path(&path, i, child), next_leaf_id));
        return match container {
            Container::Composite(p) => json!({
                "type": "composite",
                "path": path,
                "operator": p.operator,
                "conditions": children.collect::<Vec<_>>(),
            }),
            Container::Not(_) => json!({ "type": "not", "path": path, "child": children.next() }),
            Container::Sustained(p) => json!({
                "type": "sustained",
                "path": path,
                "minutes": p.minutes,
                "child": children.next(),
            }),
        };
    }

    let leaf_id = *next_leaf_id;
    *next_leaf_id = leaf_id.saturating_add(1);
    json!({
        "leaf_id": leaf_id,
        "path": path,
        "type": node.kind().name(),
        "kind": node.kind().name(),
        "params": node.payload(),
    })
}
