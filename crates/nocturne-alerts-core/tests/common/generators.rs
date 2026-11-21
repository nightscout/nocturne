//! Generators shared by the property suites of both crates: valid condition
//! trees and payloads for every kind, sustained-bearing rules over simple
//! leaves, tick sequences, arbitrary JSON, and near-valid mutations.

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use proptest::collection::{btree_map, vec};
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use serde_json::{Map, Value, json};
use uuid::Uuid;

use nocturne_alerts_core::engine::OnUnknown;
use nocturne_alerts_core::enums::{
    AlertStateKind, CmpOp, CompositeOp, DayOfWeek, GlucoseBucket, PumpMode, RateDirection,
    StateSpanCategory, TempBasalMetric, ThresholdDirection, TransmitterBatteryMetric, TrendBucket,
    WireEnum,
};
use nocturne_alerts_core::model::{ConditionKind, Payload};

pub(crate) const RULE_ID: Uuid = Uuid::from_u128(1);

pub(crate) fn base() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0)
        .single()
        .expect("a valid instant")
}

pub(crate) fn word<E: WireEnum>() -> BoxedStrategy<&'static str> {
    select(E::ALL.iter().map(|e| e.name()).collect::<Vec<_>>()).boxed()
}

pub(crate) fn node(kind: &str, payload: Value) -> Value {
    json!({ "type": kind, kind: payload })
}

pub(crate) fn simple_leaf() -> BoxedStrategy<Value> {
    prop_oneof![
        (word::<ThresholdDirection>(), 40..300i32)
            .prop_map(|(d, v)| node("threshold", json!({ "direction": d, "value": v }))),
        (word::<CmpOp>(), 0..6i32)
            .prop_map(|(op, v)| node("iob", json!({ "operator": op, "value": v }))),
        any::<bool>().prop_map(|a| node("sleep_session_active", json!({ "is_active": a }))),
    ]
    .boxed()
}

fn sustained(minutes: i32, child: Value) -> Value {
    node("sustained", json!({ "minutes": minutes, "child": child }))
}

pub(crate) fn composite(operator: &str, conditions: Vec<Value>) -> Value {
    node(
        "composite",
        json!({ "operator": operator, "conditions": conditions }),
    )
}

fn simple_tree() -> BoxedStrategy<Value> {
    simple_leaf()
        .prop_recursive(3, 12, 3, |inner| {
            prop_oneof![
                3 => (1..=30i32, inner.clone()).prop_map(|(m, c)| sustained(m, c)),
                2 => (word::<CompositeOp>(), vec(inner.clone(), 1..=3))
                    .prop_map(|(op, cs)| composite(op, cs)),
                1 => inner.prop_map(|c| node("not", json!({ "child": c }))),
            ]
        })
        .boxed()
}

/// A tree whose root is a composite holding a `sustained` node.
fn sustained_tree() -> BoxedStrategy<Value> {
    (
        word::<CompositeOp>(),
        1..=30i32,
        simple_tree(),
        simple_tree(),
    )
        .prop_map(|(op, minutes, held, other)| composite(op, vec![sustained(minutes, held), other]))
        .boxed()
}

/// A rule whose body and auto-resolve tree are `sustained`-bearing composites.
#[derive(Debug, Clone)]
pub(crate) struct RuleSpec {
    body: Value,
    confirmation_readings: i32,
    hysteresis_minutes: i32,
    auto_resolve: Option<Value>,
    on_unknown: &'static str,
}

impl RuleSpec {
    /// The rule in the corpus `ScenarioRule` shape.
    pub(crate) fn wire(&self) -> Value {
        let params = self.body.get("composite").cloned();
        json!({
            "id": RULE_ID.to_string(),
            "condition_type": "composite",
            "condition_params": params,
            "confirmation_readings": self.confirmation_readings,
            "hysteresis_minutes": self.hysteresis_minutes,
            "auto_resolve_enabled": self.auto_resolve.is_some(),
            "auto_resolve_params": self.auto_resolve,
            "on_unknown": self.on_unknown,
        })
    }
}

/// An `on_unknown` word, `hold` half the time as the default it is.
pub(crate) fn on_unknown() -> BoxedStrategy<&'static str> {
    prop_oneof![1 => Just(OnUnknown::Hold.name()), 1 => word::<OnUnknown>()].boxed()
}

pub(crate) fn rule_spec() -> impl Strategy<Value = RuleSpec> {
    (
        sustained_tree(),
        0..4i32,
        0..10i32,
        option::of(sustained_tree()),
        on_unknown(),
    )
        .prop_map(
            |(body, confirmation_readings, hysteresis_minutes, auto_resolve, on_unknown)| {
                RuleSpec {
                    body,
                    confirmation_readings,
                    hysteresis_minutes,
                    auto_resolve,
                    on_unknown,
                }
            },
        )
}

/// One evaluation instant and the context it reads.
#[derive(Debug, Clone)]
pub(crate) struct Tick {
    pub(crate) now: DateTime<Utc>,
    pub(crate) context: Value,
}

pub(crate) fn ticks() -> impl Strategy<Value = Vec<Tick>> {
    vec(
        (
            prop_oneof![1 => 0..=1800i64, 1 => (0..=30i64).prop_map(|m| m * 60)],
            option::of(40..300i32),
            option::of(0..6i32),
            any::<bool>(),
        ),
        1..=24,
    )
    .prop_map(|steps| {
        let mut now = base();
        steps
            .into_iter()
            .map(|(seconds, glucose, iob, asleep)| {
                now += TimeDelta::seconds(seconds);
                let mut context = json!({
                    "latest_timestamp": now.to_rfc3339(),
                    "sleep_session_active": asleep,
                });
                if let Some(glucose) = glucose {
                    context["latest_value"] = glucose.into();
                }
                if let Some(iob) = iob {
                    context["iob_units"] = iob.into();
                }
                Tick { now, context }
            })
            .collect()
    })
}

/// Names a document is likely to hold: kind names, every payload field, and
/// the node keys.
pub(crate) fn known_keys() -> Vec<&'static str> {
    let mut keys = vec!["type", "_uid", "child", "conditions"];
    for kind in ConditionKind::ALL {
        keys.push(kind.name());
        keys.extend_from_slice(Payload::fields(*kind));
    }
    keys.sort_unstable();
    keys.dedup();
    keys
}

/// Arbitrary JSON up to five containers deep, leaning on the model's own
/// words so that some of it reads partway.
pub(crate) fn arbitrary_json() -> BoxedStrategy<Value> {
    let keys = known_keys();
    let words: Vec<&'static str> = keys
        .iter()
        .copied()
        .chain([">=", "above", "and", "06:30", "UTC", "pump_mode", "bogus"])
        .collect();
    let leaf = prop_oneof![
        Just(Value::Null),
        any::<bool>().prop_map(Value::from),
        any::<i64>().prop_map(Value::from),
        any::<u64>().prop_map(Value::from),
        (-1.0e6..1.0e6f64).prop_map(Value::from),
        "[ -~]{0,8}".prop_map(Value::from),
        select(words).prop_map(Value::from),
    ];
    let key = prop_oneof![3 => select(keys).prop_map(str::to_owned), 1 => "[a-z_]{1,6}"];
    leaf.prop_recursive(5, 48, 4, move |inner| {
        prop_oneof![
            vec(inner.clone(), 0..4).prop_map(Value::Array),
            btree_map(key.clone(), inner, 0..5)
                .prop_map(|fields| Value::Object(fields.into_iter().collect())),
        ]
    })
    .boxed()
}

pub(crate) fn number() -> BoxedStrategy<Value> {
    prop_oneof![
        (-100_000..100_000i32).prop_map(Value::from),
        (-100_000..100_000i32).prop_map(|n| Value::from(f64::from(n) / 100.0)),
    ]
    .boxed()
}

pub(crate) fn uuid_text() -> BoxedStrategy<Value> {
    any::<u128>()
        .prop_map(|n| Value::from(Uuid::from_u128(n).to_string()))
        .boxed()
}

pub(crate) fn word_value<E: WireEnum>() -> BoxedStrategy<Value> {
    word::<E>().prop_map(Value::from).boxed()
}

pub(crate) fn words<E: WireEnum>() -> BoxedStrategy<Value> {
    vec(word::<E>(), 1..4).prop_map(Value::from).boxed()
}

/// Absent, `null` (which reads as absent), or a value.
pub(crate) fn optional(value: BoxedStrategy<Value>) -> BoxedStrategy<Option<Value>> {
    prop_oneof![
        1 => Just(None),
        1 => Just(Some(Value::Null)),
        3 => value.prop_map(Some),
    ]
    .boxed()
}

pub(crate) fn object(fields: Vec<(&str, Option<Value>)>) -> Value {
    fields
        .into_iter()
        .filter_map(|(key, value)| Some((key.to_owned(), value?)))
        .collect::<Map<_, _>>()
        .into()
}

pub(crate) fn minutes(range: std::ops::RangeInclusive<i32>) -> BoxedStrategy<Value> {
    range.prop_map(Value::from).boxed()
}

pub(crate) fn for_minutes() -> BoxedStrategy<Option<Value>> {
    optional(minutes(-5..=1500))
}

pub(crate) fn compare() -> BoxedStrategy<Value> {
    (word_value::<CmpOp>(), number())
        .prop_map(|(operator, value)| json!({ "operator": operator, "value": value }))
        .boxed()
}

pub(crate) fn minutes_compare(field: &'static str) -> BoxedStrategy<Value> {
    (word_value::<CmpOp>(), minutes(0..=1440))
        .prop_map(move |(operator, m)| json!({ "operator": operator, field: m }))
        .boxed()
}

pub(crate) fn active_for() -> BoxedStrategy<Value> {
    (any::<bool>(), for_minutes())
        .prop_map(|(is_active, for_minutes)| {
            object(vec![
                ("is_active", Some(is_active.into())),
                ("for_minutes", for_minutes),
            ])
        })
        .boxed()
}

pub(crate) fn time_of_day() -> BoxedStrategy<Value> {
    let zone = select(vec!["UTC", "Australia/Sydney", "America/New_York"]).prop_map(Value::from);
    (0..1440u32, 1..1440u32, optional(zone.boxed()))
        .prop_map(|(from, gap, timezone)| {
            let clock = |m: u32| Value::from(format!("{:02}:{:02}", m / 60, m % 60));
            object(vec![
                ("from", Some(clock(from))),
                ("to", Some(clock((from + gap) % 1440))),
                ("timezone", timezone),
            ])
        })
        .boxed()
}

pub(crate) fn state_span() -> BoxedStrategy<Value> {
    let categories: Vec<&'static str> = StateSpanCategory::ALL
        .iter()
        .filter(|c| **c != StateSpanCategory::PumpMode)
        .map(|c| c.name())
        .collect();
    (
        select(categories),
        optional("[a-z]{0,6}".prop_map(Value::from).boxed()),
        any::<bool>(),
        for_minutes(),
    )
        .prop_map(|(category, state, is_active, for_minutes)| {
            object(vec![
                ("category", Some(category.into())),
                ("state", state),
                ("is_active", Some(is_active.into())),
                ("for_minutes", for_minutes),
            ])
        })
        .boxed()
}

/// A valid payload of `kind`; `child` supplies the nodes of a container.
pub(crate) fn payload(kind: ConditionKind, child: &BoxedStrategy<Value>) -> BoxedStrategy<Value> {
    use ConditionKind as K;
    match kind {
        K::Threshold => (word_value::<ThresholdDirection>(), number())
            .prop_map(|(direction, value)| json!({ "direction": direction, "value": value }))
            .boxed(),
        K::RateOfChange => (word_value::<RateDirection>(), number())
            .prop_map(|(direction, rate)| json!({ "direction": direction, "rate": rate }))
            .boxed(),
        K::SignalLoss => minutes(1..=1440)
            .prop_map(|m| json!({ "timeout_minutes": m }))
            .boxed(),
        K::Composite => (word_value::<CompositeOp>(), vec(child.clone(), 1..4))
            .prop_map(|(operator, conditions)| {
                json!({ "operator": operator, "conditions": conditions })
            })
            .boxed(),
        K::Not => child
            .clone()
            .prop_map(|child| json!({ "child": child }))
            .boxed(),
        K::Sustained => (minutes(1..=1440), child.clone())
            .prop_map(|(m, child)| json!({ "minutes": m, "child": child }))
            .boxed(),
        K::Staleness => minutes_compare("value"),
        K::Predicted => (word_value::<CmpOp>(), number(), minutes(1..=240))
            .prop_map(|(operator, value, within)| {
                json!({ "operator": operator, "value": value, "within_minutes": within })
            })
            .boxed(),
        K::Trend => word_value::<TrendBucket>()
            .prop_map(|bucket| json!({ "bucket": bucket }))
            .boxed(),
        K::TimeOfDay => time_of_day(),
        K::Iob
        | K::Cob
        | K::Reservoir
        | K::SiteAge
        | K::SensorAge
        | K::PumpBattery
        | K::UploaderBattery
        | K::SensitivityRatio => compare(),
        K::AlertState => (uuid_text(), word_value::<AlertStateKind>(), for_minutes())
            .prop_map(|(alert_id, state, for_minutes)| {
                object(vec![
                    ("alert_id", Some(alert_id)),
                    ("state", Some(state)),
                    ("for_minutes", for_minutes),
                ])
            })
            .boxed(),
        K::LoopStale | K::LoopEnactionStale => minutes_compare("minutes"),
        K::PumpSuspended | K::OverrideActive | K::DoNotDisturb => active_for(),
        K::TempBasal => (
            word_value::<TempBasalMetric>(),
            word_value::<CmpOp>(),
            number(),
        )
            .prop_map(|(metric, operator, value)| {
                json!({ "metric": metric, "operator": operator, "value": value })
            })
            .boxed(),
        K::GlucoseBucket => words::<GlucoseBucket>()
            .prop_map(|buckets| json!({ "buckets": buckets }))
            .boxed(),
        K::TimeSinceLastCarb | K::TimeSinceLastBolus => minutes_compare("minutes"),
        K::DayOfWeek => words::<DayOfWeek>()
            .prop_map(|days| json!({ "days": days }))
            .boxed(),
        K::PumpState => (word_value::<PumpMode>(), any::<bool>(), for_minutes())
            .prop_map(|(mode, is_active, for_minutes)| {
                object(vec![
                    ("mode", Some(mode)),
                    ("is_active", Some(is_active.into())),
                    ("for_minutes", for_minutes),
                ])
            })
            .boxed(),
        K::StateSpanActive => state_span(),
        K::SleepSessionActive => any::<bool>()
            .prop_map(|is_active| json!({ "is_active": is_active }))
            .boxed(),
        K::TrackerAge => (uuid_text(), word_value::<CmpOp>(), minutes(-1500..=1500))
            .prop_map(|(id, operator, m)| {
                json!({ "tracker_definition_id": id, "operator": operator, "minutes": m })
            })
            .boxed(),
        K::TransmitterBattery => (
            word_value::<TransmitterBatteryMetric>(),
            word_value::<CmpOp>(),
            number(),
        )
            .prop_map(|(metric, operator, value)| {
                json!({ "metric": metric, "operator": operator, "value": value })
            })
            .boxed(),
    }
}

pub(crate) fn is_container(kind: ConditionKind) -> bool {
    matches!(
        kind,
        ConditionKind::Composite | ConditionKind::Not | ConditionKind::Sustained
    )
}

/// A valid condition node whose subtree can hold any kind.
pub(crate) fn tree() -> BoxedStrategy<Value> {
    let placeholder = Just(Value::Null).boxed();
    let leaves: Vec<ConditionKind> = ConditionKind::ALL
        .iter()
        .copied()
        .filter(|k| !is_container(*k))
        .collect();
    let leaf = select(leaves).prop_flat_map(move |kind| {
        payload(kind, &placeholder).prop_map(move |p| node(kind.name(), p))
    });
    leaf.prop_recursive(4, 24, 3, |inner| {
        select(vec![
            ConditionKind::Composite,
            ConditionKind::Not,
            ConditionKind::Sustained,
        ])
        .prop_flat_map(move |kind| payload(kind, &inner).prop_map(move |p| node(kind.name(), p)))
    })
    .boxed()
}

pub(crate) fn node_value(wire: &str, payload: Value) -> Value {
    let mut o = Map::new();
    o.insert("type".into(), wire.into());
    o.insert(wire.into(), payload);
    Value::Object(o)
}

#[derive(Debug, Clone)]
pub(crate) enum Mutation {
    DropField,
    RenameField,
    SwapJsonKind,
    ReplaceWord,
    NullSlot,
    NestDeeper(usize),
    BoundaryNumber(usize),
}

#[derive(Debug, Clone)]
pub(crate) struct Mutate {
    selector: usize,
    op: Mutation,
}

pub(crate) fn mutations(count: std::ops::Range<usize>) -> impl Strategy<Value = Vec<Mutate>> {
    let op = prop_oneof![
        Just(Mutation::DropField),
        Just(Mutation::RenameField),
        Just(Mutation::SwapJsonKind),
        Just(Mutation::ReplaceWord),
        Just(Mutation::NullSlot),
        (1..=70usize).prop_map(Mutation::NestDeeper),
        (0..4usize).prop_map(Mutation::BoundaryNumber),
    ];
    vec(
        (any::<usize>(), op).prop_map(|(selector, op)| Mutate { selector, op }),
        count,
    )
}

pub(crate) fn pointers(v: &Value, here: String, into: &mut Vec<String>) {
    into.push(here.clone());
    match v {
        Value::Array(items) => {
            for (i, item) in items.iter().enumerate() {
                pointers(item, format!("{here}/{i}"), into);
            }
        }
        Value::Object(fields) => {
            for (key, item) in fields {
                let key = key.replace('~', "~0").replace('/', "~1");
                pointers(item, format!("{here}/{key}"), into);
            }
        }
        _ => {}
    }
}

pub(crate) fn remove_or_rename(doc: &mut Value, parent: &str, key: &str, rename: bool) {
    match doc.pointer_mut(parent) {
        Some(Value::Object(fields)) => {
            if let Some(value) = fields.remove(key)
                && rename
            {
                fields.insert(format!("{key}_"), value);
            }
        }
        Some(Value::Array(items)) => {
            if let Ok(i) = key.parse::<usize>() {
                items.remove(i);
            }
        }
        _ => {}
    }
}

pub(crate) fn replace_slot(old: Value, op: &Mutation) -> Value {
    match op {
        Mutation::SwapJsonKind => match old {
            Value::String(_) => json!(1),
            Value::Number(_) => json!("1"),
            Value::Bool(_) => json!("true"),
            Value::Array(_) => json!({}),
            Value::Object(_) => json!([]),
            Value::Null => json!(0),
        },
        Mutation::ReplaceWord => match old {
            Value::String(s) if s.to_uppercase() != s => Value::from(s.to_uppercase()),
            _ => Value::from("bogus"),
        },
        Mutation::NestDeeper(levels) => {
            (0..*levels).fold(old, |inner, _| node_value("not", json!({ "child": inner })))
        }
        Mutation::BoundaryNumber(which) => match old {
            Value::Number(_) => match which {
                0 => json!(0),
                1 => json!(-1),
                2 => json!(i64::from(i32::MAX) + 1),
                _ => json!(0.5),
            },
            other => other,
        },
        Mutation::NullSlot | Mutation::DropField | Mutation::RenameField => Value::Null,
    }
}

pub(crate) fn mutate(doc: &mut Value, m: &Mutate) {
    let mut all = Vec::new();
    pointers(doc, String::new(), &mut all);
    let pointer = &all[m.selector % all.len()];
    let (parent, key) = pointer.rsplit_once('/').unwrap_or(("", ""));
    let key = key.replace("~1", "/").replace("~0", "~");

    match &m.op {
        Mutation::DropField | Mutation::RenameField => {
            if !pointer.is_empty() {
                let rename = matches!(m.op, Mutation::RenameField);
                remove_or_rename(doc, parent, &key, rename);
            }
        }
        op => {
            let slot = if pointer.is_empty() {
                doc
            } else {
                doc.pointer_mut(pointer)
                    .expect("a collected pointer resolves")
            };
            *slot = replace_slot(slot.take(), op);
        }
    }
}

pub(crate) fn mutated(mut doc: Value, ops: &[Mutate]) -> Value {
    for op in ops {
        mutate(&mut doc, op);
    }
    doc
}
