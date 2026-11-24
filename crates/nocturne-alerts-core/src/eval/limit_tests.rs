//! A limit from a fact (engine-semantics.md §3): checked against the same
//! leaf with the fact's value written as a literal.

#![allow(
    clippy::expect_used,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use chrono::{DateTime, TimeDelta, Utc};
use proptest::prelude::*;
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use super::{Env, MetPoint, eval_payload_unknown, met_point};
use crate::context::{ApsDetermination, PredictedPoint, SensorContext, SensorStateSnapshot};
use crate::enums::{CmpOp, LimitFact, SensorState, WireEnum};
use crate::facts::ContextFact;
use crate::model::{ConditionKind, Payload, Reason, parse_payload};
use crate::sustained::TimerStore;
use crate::truth::Truth;
use crate::unknown::{UnknownFacts, UnknownReason};

fn now() -> DateTime<Utc> {
    "2026-01-05T12:00:00Z".parse().expect("an instant")
}

fn ago(minutes: i64) -> DateTime<Utc> {
    now()
        .checked_sub_signed(TimeDelta::minutes(minutes))
        .expect("in range")
}

#[derive(Debug, Clone)]
struct World {
    latest: Option<Decimal>,
    reading_age: i64,
    sensor_ok: Option<bool>,
    target_low: Option<Decimal>,
    target_high: Option<Decimal>,
    predictions: Vec<(i32, Decimal)>,
    determination: Option<(i64, Option<Decimal>)>,
}

impl World {
    fn context(&self) -> SensorContext {
        SensorContext {
            latest_value: self.latest,
            latest_timestamp: self.latest.map(|_| ago(self.reading_age)),
            latest_received_at: self.latest.map(|_| ago(self.reading_age)),
            target_low: self.target_low,
            target_high: self.target_high,
            predictions: self
                .predictions
                .iter()
                .map(|&(offset_minutes, mgdl)| PredictedPoint {
                    offset_minutes,
                    mgdl,
                })
                .collect(),
            aps_determination: self.determination.map(|(age, eventual)| ApsDetermination {
                at: ago(age),
                eventual_mgdl: eventual,
                carbs_required_grams: None,
            }),
            sensor_state: self.sensor_ok.map(|ok| SensorStateSnapshot {
                state: Some(if ok {
                    SensorState::Ok
                } else {
                    SensorState::Error
                }),
                since: None,
                until: None,
            }),
            ..SensorContext::default()
        }
    }

    /// The fact's value, or why it is unknown, worked out from the world
    /// rather than by the evaluator: a reading is fresh up to 11 minutes
    /// after it arrived, and a sensor other than ok hides it.
    fn fact(&self, fact: LimitFact) -> Result<Decimal, UnknownFacts> {
        let absent = |f| UnknownFacts::of(f, UnknownReason::Absent);
        match fact {
            LimitFact::TargetLow => self.target_low.ok_or(absent(ContextFact::TargetRange)),
            LimitFact::TargetHigh => self.target_high.ok_or(absent(ContextFact::TargetRange)),
            LimitFact::LatestGlucose => match (self.sensor_ok, self.latest) {
                (Some(false), _) => Err(UnknownFacts::of(
                    ContextFact::SensorState,
                    UnknownReason::SensorNotOk,
                )),
                (_, None) => Err(absent(ContextFact::Glucose)),
                (_, Some(_)) if self.reading_age > 11 => {
                    Err(UnknownFacts::of(ContextFact::Glucose, UnknownReason::Stale))
                }
                (_, Some(value)) => Ok(value),
            },
        }
    }
}

fn mgdl() -> impl Strategy<Value = Decimal> {
    (300i64..4000, 0u32..=1).prop_map(|(n, scale)| Decimal::new(n, scale))
}

fn world() -> impl Strategy<Value = World> {
    (
        proptest::option::of(mgdl()),
        0i64..20,
        proptest::option::of(any::<bool>()),
        proptest::option::of(mgdl()),
        proptest::option::of(mgdl()),
        proptest::collection::vec((1i32..60, mgdl()), 0..4),
        proptest::option::of((0i64..20, proptest::option::of(mgdl()))),
    )
        .prop_map(
            |(
                latest,
                reading_age,
                sensor_ok,
                target_low,
                target_high,
                predictions,
                determination,
            )| {
                World {
                    latest,
                    reading_age,
                    sensor_ok,
                    target_low,
                    target_high,
                    predictions,
                    determination,
                }
            },
        )
}

#[derive(Debug, Clone)]
struct Leaf {
    kind: ConditionKind,
    /// `threshold`'s direction, else the operator.
    comparison: &'static str,
    fact: LimitFact,
    offset: Option<Decimal>,
    margin: Option<Decimal>,
}

impl Leaf {
    fn operator(&self) -> CmpOp {
        match self.comparison {
            "below" => CmpOp::Lt,
            "above" => CmpOp::Gt,
            op => CmpOp::from_name(op).expect("an operator"),
        }
    }

    fn params(&self, limit: Value) -> Value {
        let mut params = match self.kind {
            ConditionKind::Threshold => json!({ "direction": self.comparison }),
            ConditionKind::Predicted => {
                json!({ "operator": self.comparison, "within_minutes": 30 })
            }
            _ => json!({ "operator": self.comparison }),
        };
        let o = params.as_object_mut().expect("an object");
        if let Value::Object(limit) = limit {
            o.extend(limit);
        }
        if let Some(margin) = self.margin {
            o.insert("clear_margin".into(), number(margin));
        }
        params
    }

    fn by_reference(&self) -> Payload {
        let mut value_from = json!({ "fact": self.fact.name() });
        if let Some(offset) = self.offset {
            value_from["offset"] = number(offset);
        }
        parse_payload(self.kind, &self.params(json!({ "value_from": value_from })))
            .expect("the reference parses")
    }

    fn literal(&self, value: Decimal) -> Payload {
        parse_payload(self.kind, &self.params(json!({ "value": number(value) })))
            .expect("the literal parses")
    }

    /// The limit compared against: `limit` moved by the margin, as §3 moves
    /// it, toward the rule's true side.
    fn widened(&self, limit: Decimal, open: bool, negated: bool) -> Decimal {
        let Some(margin) = self.margin.filter(|_| open) else {
            return limit;
        };
        let margin = if negated { -margin } else { margin };
        match self.operator() {
            CmpOp::Lt | CmpOp::Le => limit + margin,
            CmpOp::Gt | CmpOp::Ge => limit - margin,
            CmpOp::Eq => limit,
        }
    }
}

fn number(d: Decimal) -> Value {
    Value::Number(d.to_string().parse().expect("a number"))
}

fn leaf() -> impl Strategy<Value = Leaf> {
    let kind = prop_oneof![
        Just((ConditionKind::Threshold, "below")),
        Just((ConditionKind::Threshold, "above")),
        Just((ConditionKind::Predicted, "<")),
        Just((ConditionKind::Predicted, "<=")),
        Just((ConditionKind::Predicted, ">")),
        Just((ConditionKind::Predicted, ">=")),
        Just((ConditionKind::Predicted, "==")),
        Just((ConditionKind::EventualGlucose, "<")),
        Just((ConditionKind::EventualGlucose, ">=")),
        Just((ConditionKind::EventualGlucose, "==")),
    ];
    let fact = prop_oneof![
        Just(LimitFact::LatestGlucose),
        Just(LimitFact::TargetLow),
        Just(LimitFact::TargetHigh),
    ];
    let offset =
        proptest::option::of((-1000i64..=1000, 0u32..=1).prop_map(|(n, s)| Decimal::new(n, s)));
    let margin = proptest::option::of((0i64..200, 0u32..=1).prop_map(|(n, s)| Decimal::new(n, s)));
    (kind, fact, offset, margin)
        .prop_filter(
            "threshold never compares the reading with itself",
            |((k, _), f, _, _)| !(*k == ConditionKind::Threshold && *f == LimitFact::LatestGlucose),
        )
        .prop_map(|((kind, comparison), fact, offset, margin)| Leaf {
            kind,
            comparison,
            fact,
            offset,
            margin: margin.filter(|_| comparison != "=="),
        })
}

struct Outcome {
    truth: Truth,
    unknown: UnknownFacts,
    met: Option<MetPoint>,
}

fn evaluate(payload: &Payload, ctx: &SensorContext, open: bool, negated: bool) -> Outcome {
    let mut timers = TimerStore::default();
    let mut env = Env::new(now(), Uuid::nil(), ctx, &mut timers).with_excursion_open(open);
    env.negated = negated;
    let (truth, unknown) = eval_payload_unknown(payload, payload.kind().name(), &mut env);
    let met = met_point(payload, &mut env);
    Outcome {
        truth,
        unknown,
        met,
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(2000))]

    /// A limit naming a fact whose value is `f`, with offset `o`, decides as
    /// the same leaf with the literal `f + o`, the margin included. Unknown
    /// exactly when either side is, by the facts of every unknown side.
    #[test]
    fn a_limit_from_a_fact_is_the_literal_it_stands_for(
        w in world(),
        leaf in leaf(),
        open in any::<bool>(),
        negated in any::<bool>(),
    ) {
        let ctx = w.context();
        let reference = evaluate(&leaf.by_reference(), &ctx, open, negated);
        match w.fact(leaf.fact) {
            Ok(f) => {
                let limit = f + leaf.offset.unwrap_or_default();
                let literal = evaluate(&leaf.literal(limit), &ctx, open, negated);
                prop_assert_eq!(reference.truth, literal.truth);
                prop_assert_eq!(reference.unknown, literal.unknown);
                let named = MetPoint {
                    limit: Some(leaf.widened(limit, open, negated)),
                    limit_fact: Some(leaf.fact),
                    limit_offset: leaf.offset.filter(|o| !o.is_zero()),
                    ..MetPoint::value(Decimal::ZERO)
                };
                let expected = match leaf.kind {
                    ConditionKind::Threshold => reference.truth.is_true().then(|| MetPoint {
                        value: w.latest.expect("a true threshold has a reading"),
                        ..named
                    }),
                    _ => literal.met.map(|m| MetPoint {
                        limit: named.limit,
                        limit_fact: named.limit_fact,
                        limit_offset: named.limit_offset,
                        ..m
                    }),
                };
                prop_assert_eq!(reference.met, expected);
            }
            Err(fact_unknown) => {
                let left = evaluate(&leaf.literal(Decimal::ONE_HUNDRED), &ctx, open, negated);
                prop_assert!(reference.truth.is_unknown());
                prop_assert_eq!(reference.unknown, left.unknown.union(fact_unknown));
                prop_assert_eq!(reference.met, None);
            }
        }
    }
}

fn issues(kind: ConditionKind, params: Value) -> Vec<(String, &'static str, Option<&'static str>)> {
    parse_payload(kind, &params).map_or_else(
        |e| {
            e.0.into_iter()
                .map(|i| (i.path, i.reason.code(), i.reason.field()))
                .collect()
        },
        |_| Vec::new(),
    )
}

#[test]
fn each_invalid_form_of_a_limit_is_refused_with_its_reason() {
    use ConditionKind as K;
    let cases: Vec<(K, Value, &str, Option<&str>)> = vec![
        (
            K::Threshold,
            json!({"direction": "below", "value": 70, "value_from": {"fact": "target_low"}}),
            Reason::LimitConflict.code(),
            Some("value_from"),
        ),
        (
            K::Threshold,
            json!({"direction": "below"}),
            "field_missing",
            Some("value"),
        ),
        (
            K::Predicted,
            json!({"operator": "<", "within_minutes": 30}),
            "field_missing",
            Some("value"),
        ),
        (
            K::Threshold,
            json!({"direction": "above", "value_from": {"fact": "latest_glucose"}}),
            "self_reference",
            Some("value_from"),
        ),
        (
            K::Predicted,
            json!({"operator": "<", "within_minutes": 30,
            "value_from": {"fact": "latest_glucose", "offset": 1000.1}}),
            "offset_out_of_range",
            Some("offset"),
        ),
        (
            K::EventualGlucose,
            json!({"operator": "<",
            "value_from": {"fact": "target_low", "offset": -1001}}),
            "offset_out_of_range",
            Some("offset"),
        ),
        (
            K::EventualGlucose,
            json!({"operator": "<",
            "value_from": {"fact": "target_low", "offset": "10"}}),
            "invalid_field",
            Some("offset"),
        ),
        (
            K::EventualGlucose,
            json!({"operator": "<",
            "value_from": {"fact": "target_low", "offset": 1e40}}),
            "invalid_field",
            Some("offset"),
        ),
        (
            K::Threshold,
            json!({"direction": "below", "value_from": {"fact": "max_iob"}}),
            "unknown_value",
            Some("fact"),
        ),
        (
            K::Threshold,
            json!({"direction": "below", "value_from": {}}),
            "field_missing",
            Some("fact"),
        ),
        (
            K::Threshold,
            json!({"direction": "below", "value_from": "target_low"}),
            "invalid_field",
            Some("value_from"),
        ),
        (
            K::Threshold,
            json!({"direction": "below", "value_from": {"fact": "target_low", "scale": 2}}),
            "unknown_field",
            None,
        ),
        (
            K::Iob,
            json!({"operator": "<", "value_from": {"fact": "target_low"}}),
            "unknown_field",
            None,
        ),
        (
            K::RateOfChange,
            json!({"direction": "falling", "value_from": {"fact": "latest_glucose"}}),
            "unknown_field",
            None,
        ),
        (
            K::CarbsRequired,
            json!({"operator": ">", "value_from": {"fact": "target_low"}}),
            "unknown_field",
            None,
        ),
        (
            K::Predicted,
            json!({"operator": "==", "within_minutes": 30,
            "value_from": {"fact": "target_low"}, "clear_margin": 5}),
            "margin_on_equality",
            Some("clear_margin"),
        ),
    ];
    for (kind, params, code, field) in cases {
        let found = issues(kind, params.clone());
        assert!(
            found.iter().any(|(_, c, f)| *c == code && *f == field),
            "{params}: {found:?}"
        );
    }
}

/// Clear-margin validation of §3 holds for a limit from a fact as for a
/// literal.
#[test]
fn a_margin_under_sustained_is_refused_on_a_limit_from_a_fact() {
    let tree = json!({"minutes": 15, "child": {"type": "threshold", "threshold":
        {"direction": "below", "value_from": {"fact": "target_low"}, "clear_margin": 5}}});
    assert_eq!(
        issues(ConditionKind::Sustained, tree),
        [(
            "sustained[0].threshold".to_owned(),
            "margin_under_sustained",
            Some("clear_margin")
        )]
    );
}

/// A literal serialises back to what was written, and a reference to its
/// `value_from`, in the position `value` holds.
#[test]
fn a_limit_serialises_as_it_was_written() {
    for (kind, params) in [
        (
            ConditionKind::Threshold,
            json!({"direction": "below", "value": 70.50}),
        ),
        (
            ConditionKind::Predicted,
            json!({"operator": "<", "value": 70, "within_minutes": 30, "curve": "iob"}),
        ),
        (
            ConditionKind::EventualGlucose,
            json!({"operator": ">=", "value": 250, "clear_margin": 10}),
        ),
        (
            ConditionKind::Threshold,
            json!({"direction": "below",
            "value_from": {"fact": "target_low", "offset": -10.5}}),
        ),
        (
            ConditionKind::Predicted,
            json!({"operator": "<", "within_minutes": 30,
            "value_from": {"fact": "latest_glucose"}}),
        ),
    ] {
        let payload = parse_payload(kind, &params).expect("parses");
        assert_eq!(
            serde_json::to_value(&payload).expect("serialises"),
            params,
            "{kind:?}"
        );
    }
}

/// A `sustained` over a comparison with a target starts its run at the evaluation, since the
/// target can move after the reading; over the same comparison with a literal it starts at the
/// reading (engine-semantics.md §3, a limit from a fact).
#[test]
fn a_run_over_a_target_starts_at_the_evaluation_and_over_a_literal_at_the_reading() {
    let ctx = SensorContext {
        latest_value: Some(Decimal::from(70)),
        latest_timestamp: Some(ago(3)),
        latest_received_at: Some(now()),
        target_low: Some(Decimal::from(80)),
        ..SensorContext::default()
    };
    let run_start = |child: Value| {
        let payload = parse_payload(
            ConditionKind::Sustained,
            &json!({ "minutes": 15, "child": child }),
        )
        .expect("parses");
        let mut timers = TimerStore::default();
        let mut env = Env::new(now(), Uuid::nil(), &ctx, &mut timers)
            .with_first_evaluation_bound(Some(ago(60)));
        eval_payload_unknown(&payload, "sustained", &mut env);
        timers.get_first_true(Uuid::nil(), "sustained")
    };

    let target = json!({"type": "threshold", "threshold":
        {"direction": "below", "value_from": {"fact": "target_low"}}});
    let literal = json!({"type": "threshold", "threshold": {"direction": "below", "value": 80}});

    assert_eq!(run_start(target), Some(now()));
    assert_eq!(run_start(literal), Some(ago(3)));
}
