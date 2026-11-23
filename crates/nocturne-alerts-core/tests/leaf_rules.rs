//! Each leaf's rule in engine-semantics.md §3, stated as a model over the JSON of a context that
//! fills every fact, and checked against the evaluator: true, false and unknown, with the fact
//! present, absent, stale or guarded off.
//!
//! Every instant is at `generators::base()`, a Monday at 12:00 UTC, so a daylight saving change and
//! most day boundaries are not crossed. Elapsed times are whole seconds and the model compares
//! them exactly. `site_age` and `sensor_age` take operands in tenths and ages in tenths of their
//! unit, where the evaluator's double-to-decimal conversion (§1.3) decides equality: one tenth
//! of an hour is not a double. The other leaves take whole-number operands, as their payloads do.

#![allow(
    dead_code,
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code, and generators of which this suite reads some"
)]

#[path = "common/full_context.rs"]
mod full_context;
#[path = "common/generators.rs"]
mod generators;

use std::collections::BTreeMap;
use std::str::FromStr;

use chrono::{DateTime, Datelike, TimeDelta, Timelike, Utc, Weekday};
use chrono_tz::Tz;
use proptest::collection::vec;
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use proptest::strategy::ValueTree;
use proptest::test_runner::{Config, TestRunner};
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, WireRule, evaluate_rule};
use nocturne_alerts_core::enums::{
    AlertStateKind, CmpOp, DayOfWeek, GlucoseBucket, PumpMode, RateDirection, SensorState,
    StateSpanCategory, TempBasalMetric, ThresholdDirection, TransmitterBatteryMetric, TrendBucket,
    WireEnum,
};
use nocturne_alerts_core::eval::{Env, eval_payload};
use nocturne_alerts_core::model::{ConditionKind, parse_payload};
use nocturne_alerts_core::sustained::TimerStore;
use nocturne_alerts_core::truth::Truth;

use full_context::{ALERT_IDS, TRACKER_IDS, age_in, full_context, pump_modes, quantity};
use generators::{base, object, word, word_value, words};

const FRESHNESS_MINUTES: i64 = 11;
const TOLERANCE_SECONDS: i64 = 300;
const SAMPLES: usize = 2000;
const MIN_REACH: usize = SAMPLES / 100;

#[derive(Debug, Clone)]
struct Case {
    ctx: Value,
    payload: Value,
    freshness_minutes: Option<i64>,
    enabled_ago: Option<i64>,
}

fn case(ctx: Value, payload: Value) -> Case {
    Case {
        ctx,
        payload,
        freshness_minutes: None,
        enabled_ago: None,
    }
}

fn leaf(payload: BoxedStrategy<Value>) -> BoxedStrategy<Case> {
    (full_context(), payload)
        .prop_map(|(ctx, payload)| case(ctx, payload))
        .boxed()
}

fn evaluate(kind: ConditionKind, c: &Case) -> Truth {
    let context: SensorContext = serde_json::from_value(c.ctx.clone()).expect("a context");
    let payload = parse_payload(kind, &c.payload).expect("a payload");
    let mut timers = TimerStore::new();
    let mut env = Env::new(base(), Uuid::nil(), &context, &mut timers)
        .with_glucose_freshness(TimeDelta::minutes(
            c.freshness_minutes.unwrap_or(FRESHNESS_MINUTES),
        ))
        .with_enabled_at(c.enabled_ago.map(|ago| base() - TimeDelta::seconds(ago)));
    eval_payload(&payload, kind.name(), &mut env)
}

/// The truth the rule gives and the branch of the rule that gave it.
type Verdict = (Truth, String);

type Model = fn(&Case) -> Verdict;

fn verdict(truth: impl Into<Truth>, branch: &str) -> Verdict {
    (truth.into(), branch.to_owned())
}

fn unknown(branch: &str) -> Verdict {
    verdict(Truth::Unknown, branch)
}

fn word_of(t: Truth) -> &'static str {
    match t {
        Truth::True => "true",
        Truth::False => "false",
        Truth::Unknown => "unknown",
    }
}

/// Runs the property over the strategy, then reports how often each branch of the model is
/// reached over `SAMPLES` cases and requires `reaches` each at least 1% of them.
fn run(kind: ConditionKind, strategy: BoxedStrategy<Case>, model: Model, reaches: &[&str]) {
    TestRunner::new(Config {
        cases: 512,
        ..Config::default()
    })
    .run(&strategy, |c| {
        let (expected, branch) = model(&c);
        let actual = evaluate(kind, &c);
        prop_assert_eq!(
            actual,
            expected,
            "{} ({}): {}",
            kind.name(),
            branch,
            serde_json::to_string(&json!({ "context": c.ctx, "payload": c.payload })).unwrap()
        );
        Ok(())
    })
    .unwrap_or_else(|e| panic!("{e}"));

    let mut runner = TestRunner::deterministic();
    let mut seen: BTreeMap<String, usize> = BTreeMap::new();
    for _ in 0..SAMPLES {
        let c = strategy.new_tree(&mut runner).unwrap().current();
        let (truth, branch) = model(&c);
        *seen
            .entry(format!("{}:{branch}", word_of(truth)))
            .or_default() += 1;
    }
    println!("{}: {seen:?}", kind.name());
    for branch in reaches {
        let reached = seen.get(*branch).copied().unwrap_or(0);
        assert!(
            reached >= MIN_REACH,
            "{}: `{branch}` reached in {reached} of {SAMPLES} cases, in {seen:?}",
            kind.name()
        );
    }
}

macro_rules! family {
    ($name:ident, $kind:expr, $strategy:expr, $model:expr, [$($branch:literal),+ $(,)?]) => {
        #[test]
        fn $name() {
            run($kind, $strategy, $model, &[$($branch),+]);
        }
    };
}

fn field<'a>(ctx: &'a Value, name: &str) -> Option<&'a Value> {
    ctx.get(name).filter(|v| !v.is_null())
}

fn decimal_of(v: &Value) -> Decimal {
    Decimal::from_str(&v.to_string()).expect("a decimal")
}

fn decimal(ctx: &Value, name: &str) -> Option<Decimal> {
    field(ctx, name).map(decimal_of)
}

fn flag(ctx: &Value, name: &str) -> bool {
    field(ctx, name).and_then(Value::as_bool).unwrap_or(false)
}

fn ago_of_instant(v: &Value) -> i64 {
    let at: DateTime<Utc> = v.as_str().expect("an instant").parse().expect("an instant");
    (base() - at).num_seconds()
}

fn ago(ctx: &Value, name: &str) -> Option<i64> {
    field(ctx, name).map(ago_of_instant)
}

fn started_ago(ctx: &Value, name: &str) -> Option<i64> {
    field(ctx, name).map(|span| ago_of_instant(&span["started_at"]))
}

/// §5.4: an anchor ahead of `now` by no more than the tolerance has passed no time, and one
/// further ahead has no known elapsed time.
fn passed(ago: i64) -> Option<i64> {
    match ago {
        a if a >= 0 => Some(a),
        a if a >= -TOLERANCE_SECONDS => Some(0),
        _ => None,
    }
}

fn compare<T: PartialOrd>(op: &str, actual: T, threshold: T) -> bool {
    match op {
        ">" => actual > threshold,
        ">=" => actual >= threshold,
        "<" => actual < threshold,
        "<=" => actual <= threshold,
        "==" => actual == threshold,
        other => panic!("the generators produce no operator {other}"),
    }
}

fn operator(c: &Case) -> &str {
    c.payload["operator"].as_str().expect("an operator")
}

fn operand(c: &Case, name: &str) -> Decimal {
    decimal_of(&c.payload[name])
}

fn whole(c: &Case, name: &str) -> i64 {
    c.payload[name].as_i64().expect("a whole number")
}

fn compared(c: &Case, actual: Decimal) -> Truth {
    compare(operator(c), actual, operand(c, "value")).into()
}

fn compare_payload() -> BoxedStrategy<Value> {
    (word_value::<CmpOp>(), quantity())
        .prop_map(|(operator, value)| json!({ "operator": operator, "value": value }))
        .boxed()
}

fn whole_compare_payload(
    field: &'static str,
    range: std::ops::RangeInclusive<i32>,
) -> BoxedStrategy<Value> {
    (word_value::<CmpOp>(), range)
        .prop_map(move |(operator, n)| json!({ "operator": operator, field: n }))
        .boxed()
}

fn tenths_compare_payload(range: std::ops::RangeInclusive<i32>) -> BoxedStrategy<Value> {
    (word_value::<CmpOp>(), range)
        .prop_map(|(operator, n)| {
            json!({ "operator": operator, "value": Value::from(f64::from(n) / 10.0) })
        })
        .boxed()
}

fn active_payload() -> BoxedStrategy<Value> {
    (any::<bool>(), option::of(-1..=5i32))
        .prop_map(|(is_active, for_minutes)| {
            object(vec![
                ("is_active", Some(is_active.into())),
                ("for_minutes", for_minutes.map(Value::from)),
            ])
        })
        .boxed()
}

// ---------------------------------------------------------------------------
// glucose facts, judged for freshness
// ---------------------------------------------------------------------------

fn freshness() -> BoxedStrategy<Option<i64>> {
    prop_oneof![1 => Just(None), 1 => (1..=30i64).prop_map(Some)].boxed()
}

fn glucose_leaf(payload: BoxedStrategy<Value>) -> BoxedStrategy<Case> {
    (full_context(), payload, freshness())
        .prop_map(|(ctx, payload, freshness_minutes)| Case {
            freshness_minutes,
            ..case(ctx, payload)
        })
        .boxed()
}

/// A reading is stale once more than the bound has passed since it arrived, else since its
/// timestamp, and is not judged with neither; a timestamp further ahead than the tolerance is
/// not known to be fresh.
fn glucose_age(c: &Case) -> Truth {
    let bound = c.freshness_minutes.unwrap_or(FRESHNESS_MINUTES) * 60;
    if let Some(arrived) = ago(&c.ctx, "latest_received_at") {
        return (arrived > bound).into();
    }
    ago(&c.ctx, "latest_timestamp").map_or(Truth::False, |at| {
        passed(at).map_or(Truth::Unknown, |p| (p > bound).into())
    })
}

/// §3, sensor state: a state other than ok, or a name the crate has no member for, leaves the
/// leaf unknown whatever the reading; absent and ok read as no state.
fn sensor_not_ok(ctx: &Value) -> bool {
    sensor_state_now(ctx).is_some_and(|s| s["state"] != "ok")
}

/// §4: the sensor's state, unless its `until` is at or before now.
fn sensor_state_now(ctx: &Value) -> Option<&Value> {
    field(ctx, "sensor_state").filter(|s| field(s, "until").is_none_or(|u| ago_of_instant(u) < 0))
}

/// `decide` over a fact the reading's age and the sensor's state allow.
fn judged(c: &Case, fact: Option<Value>, decide: impl FnOnce(Value) -> Verdict) -> Verdict {
    if sensor_not_ok(&c.ctx) {
        return unknown("sensor");
    }
    let Some(fact) = fact else {
        return unknown("absent");
    };
    match glucose_age(c) {
        Truth::False => decide(fact),
        Truth::True => unknown("stale"),
        Truth::Unknown => unknown("ahead"),
    }
}

fn threshold_model(c: &Case) -> Verdict {
    judged(c, field(&c.ctx, "latest_value").cloned(), |v| {
        let latest = decimal_of(&v);
        let value = operand(c, "value");
        match c.payload["direction"].as_str().unwrap() {
            "above" => verdict(latest > value, "above"),
            _ => verdict(latest < value, "below"),
        }
    })
}

family!(
    threshold_is_strict_over_a_fresh_reading_and_unknown_without_one,
    ConditionKind::Threshold,
    glucose_leaf(
        (word_value::<ThresholdDirection>(), quantity())
            .prop_map(|(direction, value)| json!({ "direction": direction, "value": value }))
            .boxed()
    ),
    threshold_model,
    [
        "true:above",
        "false:above",
        "true:below",
        "false:below",
        "unknown:absent",
        "unknown:stale",
        "unknown:sensor"
    ]
);

fn rate_model(c: &Case) -> Verdict {
    judged(c, field(&c.ctx, "trend_rate").cloned(), |v| {
        let rate = decimal_of(&v);
        let by = operand(c, "rate");
        match c.payload["direction"].as_str().unwrap() {
            "falling" => verdict(rate <= -by, "falling"),
            _ => verdict(rate >= by, "rising"),
        }
    })
}

family!(
    rate_of_change_is_inclusive_over_a_fresh_reading_and_unknown_without_one,
    ConditionKind::RateOfChange,
    glucose_leaf(
        (word_value::<RateDirection>(), quantity())
            .prop_map(|(direction, rate)| json!({ "direction": direction, "rate": rate }))
            .boxed()
    ),
    rate_model,
    [
        "true:falling",
        "false:falling",
        "true:rising",
        "false:rising",
        "unknown:absent",
        "unknown:stale",
        "unknown:sensor"
    ]
);

fn named(ctx: &Value, name: &str, recognised: fn(&str) -> bool) -> Option<Value> {
    field(ctx, name)
        .filter(|v| recognised(v.as_str().expect("a name")))
        .cloned()
}

fn trend_model(c: &Case) -> Verdict {
    let known = named(&c.ctx, "trend_bucket", |n| {
        TrendBucket::from_name(n).is_some()
    });
    judged(c, known, |bucket| {
        verdict(bucket == c.payload["bucket"], "bucket")
    })
}

family!(
    trend_is_the_equality_of_bucket_names_and_unknown_for_an_unrecognised_one,
    ConditionKind::Trend,
    glucose_leaf(
        word_value::<TrendBucket>()
            .prop_map(|bucket| json!({ "bucket": bucket }))
            .boxed()
    ),
    trend_model,
    [
        "true:bucket",
        "false:bucket",
        "unknown:absent",
        "unknown:stale",
        "unknown:sensor"
    ]
);

fn glucose_bucket_model(c: &Case) -> Verdict {
    let known = named(&c.ctx, "glucose_bucket", |n| {
        GlucoseBucket::from_name(n).is_some()
    });
    judged(c, known, |bucket| {
        let listed = c.payload["buckets"].as_array().unwrap().contains(&bucket);
        verdict(listed, "membership")
    })
}

family!(
    glucose_bucket_is_set_membership_and_unknown_for_an_unrecognised_bucket,
    ConditionKind::GlucoseBucket,
    glucose_leaf(
        words::<GlucoseBucket>()
            .prop_map(|buckets| json!({ "buckets": buckets }))
            .boxed()
    ),
    glucose_bucket_model,
    [
        "true:membership",
        "false:membership",
        "unknown:absent",
        "unknown:stale",
        "unknown:sensor"
    ]
);

fn predicted_model(c: &Case) -> Verdict {
    let points = field(&c.ctx, "predictions")
        .and_then(Value::as_array)
        .filter(|points| !points.is_empty());
    let Some(points) = points else {
        return unknown("none");
    };
    let within = whole(c, "within_minutes");
    let value = operand(c, "value");
    let hit = points.iter().any(|point| {
        point["offset_minutes"].as_i64().unwrap() <= within
            && compare(operator(c), decimal_of(&point["mgdl"]), value)
    });
    verdict(hit, "points")
}

family!(
    predicted_is_true_when_any_point_within_the_horizon_satisfies_and_unknown_with_none,
    ConditionKind::Predicted,
    leaf(
        (word_value::<CmpOp>(), quantity(), 1..=10i32)
            .prop_map(|(operator, value, within)| {
                json!({ "operator": operator, "value": value, "within_minutes": within })
            })
            .boxed()
    ),
    predicted_model,
    ["true:points", "false:points", "unknown:none"]
);

/// The points of the series `name` in the context's JSON: `default` is `predictions`, any other
/// a member of `prediction_curves`. A curve under `default` there, or under a name the crate has
/// no member for, is not a series.
fn series<'a>(ctx: &'a Value, name: &str) -> Vec<&'a Value> {
    let points = if name == "default" {
        field(ctx, "predictions")
    } else {
        field(ctx, "prediction_curves").and_then(|curves| field(curves, name))
    };
    points
        .and_then(Value::as_array)
        .map(|points| points.iter().collect())
        .unwrap_or_default()
}

fn offset(point: &Value) -> i64 {
    point["offset_minutes"].as_i64().unwrap()
}

fn strings(list: &Value) -> Vec<String> {
    list.as_array()
        .unwrap()
        .iter()
        .map(|v| v.as_str().unwrap().to_owned())
        .collect()
}

/// What a `predicted` leaf reads by its definition (§3): one series, or at each offset within the
/// horizon where every listed series has a point, their lowest or highest value, with the curve
/// that supplied it (the first listed of equal values). In offset order; `None` when a series it
/// names has no points.
fn selected_points(c: &Case) -> Option<Vec<(String, i64, Decimal)>> {
    let within = whole(c, "within_minutes");
    let (names, extreme): (Vec<String>, Option<&str>) =
        if let Some(list) = field(&c.payload, "min_of") {
            (strings(list), Some("min"))
        } else if let Some(list) = field(&c.payload, "max_of") {
            (strings(list), Some("max"))
        } else {
            let curve = field(&c.payload, "curve").map_or("default", |c| c.as_str().unwrap());
            (vec![curve.to_owned()], None)
        };
    let all: Vec<Vec<&Value>> = names.iter().map(|n| series(&c.ctx, n)).collect();
    if all.iter().any(Vec::is_empty) {
        return None;
    }
    let Some(extreme) = extreme else {
        let mut points: Vec<(String, i64, Decimal)> = all[0]
            .iter()
            .filter(|p| offset(p) <= within)
            .map(|p| (names[0].clone(), offset(p), decimal_of(&p["mgdl"])))
            .collect();
        points.sort_by_key(|p| p.1);
        return Some(points);
    };
    let mut offsets: Vec<i64> = all[0].iter().map(|p| offset(p)).collect();
    offsets.sort_unstable();
    offsets.dedup();
    let combined = offsets
        .into_iter()
        .filter(|&o| o <= within && all.iter().all(|s| s.iter().any(|p| offset(p) == o)))
        .map(|o| {
            let mut best: Option<(String, Decimal)> = None;
            for (name, points) in names.iter().zip(&all) {
                for p in points.iter().filter(|p| offset(p) == o) {
                    let v = decimal_of(&p["mgdl"]);
                    let better = best.as_ref().is_none_or(|(_, held)| {
                        if extreme == "min" {
                            v < *held
                        } else {
                            v > *held
                        }
                    });
                    if better {
                        best = Some((name.clone(), v));
                    }
                }
            }
            let (name, v) = best.unwrap();
            (name, o, v)
        })
        .collect();
    Some(combined)
}

fn selected_model(c: &Case) -> Verdict {
    let combined = field(&c.payload, "min_of").is_some() || field(&c.payload, "max_of").is_some();
    let branch = if combined { "combined" } else { "one" };
    let Some(points) = selected_points(c) else {
        return unknown(branch);
    };
    let value = operand(c, "value");
    verdict(
        points.iter().any(|p| compare(operator(c), p.2, value)),
        branch,
    )
}

/// A series on a shared grid of offsets, so the listed series often meet at an offset.
fn grid_series() -> BoxedStrategy<Value> {
    vec(
        (select(vec![5i64, 10, 15]), quantity())
            .prop_map(|(offset, mgdl)| json!({ "offset_minutes": offset, "mgdl": mgdl })),
        0..=3,
    )
    .prop_map(Value::from)
    .boxed()
}

fn curve_case() -> BoxedStrategy<Case> {
    let curves = vec(option::weighted(0.75, grid_series()), 4).prop_map(|series| {
        object(
            ["iob", "cob", "uam", "zt"]
                .into_iter()
                .zip(series)
                .collect::<Vec<(&str, Option<Value>)>>(),
        )
    });
    (
        full_context(),
        option::weighted(0.75, grid_series()),
        curves,
        (word_value::<CmpOp>(), quantity(), 1..=20i32),
        generators::curve_selector(),
    )
        .prop_map(
            |(mut ctx, default, curves, (operator, value, within), selector)| {
                ctx["predictions"] = default.unwrap_or(Value::Null);
                ctx["prediction_curves"] = curves;
                let mut payload =
                    json!({ "operator": operator, "value": value, "within_minutes": within });
                if let Some((name, selected)) = selector {
                    payload[name] = selected;
                }
                case(ctx, payload)
            },
        )
        .boxed()
}

family!(
    predicted_reads_the_series_its_selector_names_and_is_unknown_without_any_of_them,
    ConditionKind::Predicted,
    curve_case(),
    selected_model,
    [
        "true:one",
        "false:one",
        "unknown:one",
        "true:combined",
        "false:combined",
        "unknown:combined"
    ]
);

fn number(d: Decimal) -> Value {
    Value::Number(d.to_string().parse().unwrap())
}

/// The one member of `met_by` a rule whose body is the single leaf of `kind` reports, if any.
fn met_by(kind: ConditionKind, c: &Case) -> Option<Value> {
    let context: SensorContext = serde_json::from_value(c.ctx.clone()).expect("a context");
    let wire: WireRule = serde_json::from_value(json!({
        "id": Uuid::nil(),
        "condition_type": kind.name(),
        "condition_params": c.payload,
        "glucose_freshness_minutes": c.freshness_minutes,
    }))
    .expect("a rule");
    let rule = Rule::try_from(wire).expect("a rule");
    let outcome = evaluate_rule(&rule, &context, base(), &mut EngineState::new(), false);
    let mut members = outcome.to_json()["met_by"]
        .as_array()
        .cloned()
        .unwrap_or_default();
    assert!(members.len() <= 1);
    members.pop()
}

/// The point a true `predicted` leaf reports is the first, in offset order, of the points its
/// definition reads that satisfies the comparison, from the curve that supplied it; a leaf that is
/// not true reports none.
#[test]
fn a_true_predicted_leaf_names_the_first_point_that_met_it() {
    TestRunner::new(Config {
        cases: 512,
        ..Config::default()
    })
    .run(&curve_case(), |c| {
        let met = met_by(ConditionKind::Predicted, &c);
        let value = operand(&c, "value");
        let expected = selected_points(&c).and_then(|points| {
            points
                .into_iter()
                .find(|p| compare(operator(&c), p.2, value))
                .map(|(curve, offset, mgdl)| {
                    json!({ "leaf_id": 0, "curve": curve, "offset_minutes": offset, "value": number(mgdl) })
                })
        });
        prop_assert_eq!(
            met.as_ref().map(|m| (m["curve"].clone(), m["offset_minutes"].clone(), decimal_of(&m["value"]))),
            expected.as_ref().map(|e| (e["curve"].clone(), e["offset_minutes"].clone(), decimal_of(&e["value"]))),
            "{}",
            serde_json::to_string(&json!({ "context": c.ctx, "payload": c.payload })).unwrap()
        );
        if let Some(met) = met {
            prop_assert_eq!(&met["leaf_id"], &json!(0));
            prop_assert_eq!(&met["kind"], &json!("predicted"));
            prop_assert!(compare(operator(&c), decimal_of(&met["value"]), value));
        }
        Ok(())
    })
    .unwrap_or_else(|e| panic!("{e}"));
}

/// The loop's newest determination (§3): none, or none of the value, is absent; past the rule's
/// freshness bound from when it was made it is stale, and further ahead than the tolerance its age
/// is not known.
fn determination_model(c: &Case, value: &str) -> Verdict {
    let Some(d) = field(&c.ctx, "aps_determination") else {
        return unknown("absent");
    };
    let Some(actual) = field(d, value).map(decimal_of) else {
        return unknown("absent");
    };
    let bound = c.freshness_minutes.unwrap_or(FRESHNESS_MINUTES) * 60;
    match passed(ago_of_instant(&d["at"])) {
        None => unknown("ahead"),
        Some(age) if age > bound => unknown("stale"),
        Some(_) => verdict(compared(c, actual), "fact"),
    }
}

/// A determination present nine times in ten, of any age around the bound, with both values
/// present three times in four.
fn determination_case() -> BoxedStrategy<Case> {
    let at = prop_oneof![
        6 => (0..=50i64).prop_map(|n| n * 30),
        3 => select(vec![659i64, 660, 661]),
        2 => select(vec![-1i64, -299, -300, -301, -3600]),
    ];
    let determination = (
        at,
        option::weighted(0.75, quantity()),
        option::weighted(0.75, quantity()),
    )
        .prop_map(|(ago, eventual, carbs)| {
            object(vec![
                (
                    "at",
                    Some(Value::from((base() - TimeDelta::seconds(ago)).to_rfc3339())),
                ),
                ("eventual_mgdl", eventual),
                ("carbs_required_grams", carbs),
            ])
        });
    (
        glucose_leaf(compare_payload()),
        option::weighted(0.9, determination),
    )
        .prop_map(|(mut c, determination)| {
            c.ctx["aps_determination"] = determination.unwrap_or(Value::Null);
            c
        })
        .boxed()
}

fn eventual_glucose_model(c: &Case) -> Verdict {
    determination_model(c, "eventual_mgdl")
}

fn carbs_required_model(c: &Case) -> Verdict {
    determination_model(c, "carbs_required_grams")
}

family!(
    eventual_glucose_compares_the_newest_determination_while_it_is_fresh,
    ConditionKind::EventualGlucose,
    determination_case(),
    eventual_glucose_model,
    [
        "true:fact",
        "false:fact",
        "unknown:absent",
        "unknown:stale",
        "unknown:ahead"
    ]
);

family!(
    carbs_required_compares_the_newest_determination_while_it_is_fresh,
    ConditionKind::CarbsRequired,
    determination_case(),
    carbs_required_model,
    [
        "true:fact",
        "false:fact",
        "unknown:absent",
        "unknown:stale",
        "unknown:ahead"
    ]
);

/// A true determination leaf reports the value it compared, with no curve or offset; a leaf that
/// is not true reports nothing.
#[test]
fn a_true_determination_leaf_names_its_value() {
    for (kind, value) in [
        (ConditionKind::EventualGlucose, "eventual_mgdl"),
        (ConditionKind::CarbsRequired, "carbs_required_grams"),
    ] {
        TestRunner::new(Config {
            cases: 256,
            ..Config::default()
        })
        .run(&determination_case(), |c| {
            let met = met_by(kind, &c);
            let (truth, _) = determination_model(&c, value);
            let expected = truth
                .is_true()
                .then(|| decimal_of(&c.ctx["aps_determination"][value]));
            prop_assert_eq!(met.as_ref().map(|m| decimal_of(&m["value"])), expected);
            if let Some(met) = met {
                prop_assert_eq!(met.as_object().unwrap().len(), 3, "{}", met);
                prop_assert_eq!(&met["kind"], &json!(kind.name()));
            }
            Ok(())
        })
        .unwrap_or_else(|e| panic!("{e}"));
    }
}

// ---------------------------------------------------------------------------
// data stopped
// ---------------------------------------------------------------------------

fn enabled_case(payload: BoxedStrategy<Value>) -> BoxedStrategy<Case> {
    (
        full_context(),
        payload,
        option::weighted(0.5, full_context::age_in(60)),
    )
        .prop_map(|(ctx, payload, enabled_ago)| Case {
            enabled_ago,
            ..case(ctx, payload)
        })
        .boxed()
}

enum Stopped {
    At(i64),
    Forever,
    Nothing,
}

/// Data stopped at the newest reading's time, forever when a reading has no time, else at the
/// rule's enablement.
fn stopped(c: &Case) -> Stopped {
    if let Some(at) = ago(&c.ctx, "last_reading_at") {
        Stopped::At(at)
    } else if field(&c.ctx, "latest_timestamp").is_some() {
        Stopped::Forever
    } else {
        c.enabled_ago.map_or(Stopped::Nothing, Stopped::At)
    }
}

fn staleness_model(c: &Case) -> Verdict {
    match stopped(c) {
        Stopped::At(at) => passed(at).map_or_else(
            || unknown("ahead"),
            |p| {
                verdict(
                    compared(c, Decimal::from(p) / Decimal::from(60)),
                    "measured",
                )
            },
        ),
        Stopped::Forever => verdict(matches!(operator(c), ">" | ">="), "forever"),
        Stopped::Nothing => unknown("nothing"),
    }
}

family!(
    staleness_measures_minutes_since_data_stopped,
    ConditionKind::Staleness,
    enabled_case(whole_compare_payload("value", 0..=10)),
    staleness_model,
    [
        "true:measured",
        "false:measured",
        "true:forever",
        "false:forever",
        "unknown:nothing"
    ]
);

fn signal_loss_model(c: &Case) -> Verdict {
    let timeout = whole(c, "timeout_minutes") * 60;
    match stopped(c) {
        Stopped::At(at) => {
            passed(at).map_or_else(|| unknown("ahead"), |p| verdict(p >= timeout, "measured"))
        }
        Stopped::Forever => verdict(true, "forever"),
        Stopped::Nothing => unknown("nothing"),
    }
}

family!(
    signal_loss_is_true_once_the_timeout_has_passed_since_data_stopped,
    ConditionKind::SignalLoss,
    enabled_case(
        (1..=10i32)
            .prop_map(|m| json!({ "timeout_minutes": m }))
            .boxed()
    ),
    signal_loss_model,
    [
        "true:measured",
        "false:measured",
        "true:forever",
        "unknown:nothing"
    ]
);

// ---------------------------------------------------------------------------
// quantities
// ---------------------------------------------------------------------------

fn compare_fact(c: &Case, name: &str) -> Verdict {
    decimal(&c.ctx, name).map_or_else(|| unknown("absent"), |v| verdict(compared(c, v), "fact"))
}

fn iob_model(c: &Case) -> Verdict {
    compare_fact(c, "iob_units")
}

fn cob_model(c: &Case) -> Verdict {
    compare_fact(c, "cob_grams")
}

family!(
    iob_compares_the_fact_and_is_unknown_without_it,
    ConditionKind::Iob,
    leaf(compare_payload()),
    iob_model,
    ["true:fact", "false:fact", "unknown:absent"]
);

family!(
    cob_compares_the_fact_and_is_unknown_without_it,
    ConditionKind::Cob,
    leaf(compare_payload()),
    cob_model,
    ["true:fact", "false:fact", "unknown:absent"]
);

/// A lower-bound reading `b` decides a comparison only where every amount from `b` up gives the
/// same answer.
fn reservoir_model(c: &Case) -> Verdict {
    let Some(bound) = decimal(&c.ctx, "reservoir_units") else {
        return unknown("absent");
    };
    let value = operand(c, "value");
    if !flag(&c.ctx, "reservoir_is_lower_bound") {
        return verdict(compared(c, bound), "exact");
    }
    let decided = match operator(c) {
        ">" => (bound > value).then_some(true),
        ">=" => (bound >= value).then_some(true),
        "<" => (bound >= value).then_some(false),
        "<=" | "==" => (bound > value).then_some(false),
        other => panic!("the generators produce no operator {other}"),
    };
    decided.map_or_else(|| unknown("undecided"), |t| verdict(t, "decided"))
}

family!(
    reservoir_is_exact_or_decided_by_a_lower_bound_only_where_every_amount_agrees,
    ConditionKind::Reservoir,
    leaf(compare_payload()),
    reservoir_model,
    [
        "true:exact",
        "false:exact",
        "true:decided",
        "false:decided",
        "unknown:undecided",
        "unknown:absent"
    ]
);

fn guarded(c: &Case, guard: &str, fact: &str) -> Verdict {
    if !flag(&c.ctx, guard) {
        return unknown("unguarded");
    }
    compare_fact(c, fact)
}

fn pump_battery_model(c: &Case) -> Verdict {
    guarded(c, "has_ever_pump_snapshot", "pump_battery_percent")
}

fn uploader_battery_model(c: &Case) -> Verdict {
    guarded(c, "has_ever_uploader_snapshot", "uploader_battery_percent")
}

fn sensitivity_ratio_model(c: &Case) -> Verdict {
    guarded(c, "has_ever_aps_sensitivity", "sensitivity_ratio")
}

family!(
    pump_battery_is_unknown_until_a_pump_has_reported_and_then_compares_the_fact,
    ConditionKind::PumpBattery,
    leaf(compare_payload()),
    pump_battery_model,
    [
        "true:fact",
        "false:fact",
        "unknown:absent",
        "unknown:unguarded"
    ]
);

family!(
    uploader_battery_is_unknown_until_an_uploader_has_reported_and_then_compares_the_fact,
    ConditionKind::UploaderBattery,
    leaf(compare_payload()),
    uploader_battery_model,
    [
        "true:fact",
        "false:fact",
        "unknown:absent",
        "unknown:unguarded"
    ]
);

family!(
    sensitivity_ratio_is_unknown_until_the_loop_has_reported_one_and_then_compares_the_fact,
    ConditionKind::SensitivityRatio,
    leaf(compare_payload()),
    sensitivity_ratio_model,
    [
        "true:fact",
        "false:fact",
        "unknown:absent",
        "unknown:unguarded"
    ]
);

fn transmitter_model(c: &Case) -> Verdict {
    let fact = format!("transmitter_{}", c.payload["metric"].as_str().unwrap());
    compare_fact(c, &fact)
}

family!(
    transmitter_battery_compares_the_reading_the_metric_names_with_no_guard,
    ConditionKind::TransmitterBattery,
    leaf(
        (
            word_value::<TransmitterBatteryMetric>(),
            word_value::<CmpOp>(),
            quantity()
        )
            .prop_map(|(metric, operator, value)| {
                json!({ "metric": metric, "operator": operator, "value": value })
            })
            .boxed()
    ),
    transmitter_model,
    ["true:fact", "false:fact", "unknown:absent"]
);

/// No active temp basal is false for either metric. The percentage is `rate / scheduled * 100`
/// in decimal, unknown without a scheduled rate or with a zero one.
fn temp_basal_model(c: &Case) -> Verdict {
    let Some(temp) = field(&c.ctx, "active_temp_basal") else {
        return verdict(false, "none");
    };
    let rate = decimal_of(&temp["rate"]);
    match c.payload["metric"].as_str().unwrap() {
        "rate" => verdict(compared(c, rate), "rate"),
        _ => match field(temp, "scheduled_rate").map(decimal_of) {
            Some(scheduled) if !scheduled.is_zero() => verdict(
                compared(c, rate / scheduled * Decimal::ONE_HUNDRED),
                "percent",
            ),
            _ => unknown("unscheduled"),
        },
    }
}

fn temp_basal_payload() -> BoxedStrategy<Value> {
    let percent = select(vec![-200, -100, -50, 0, 50, 100, 150, 200, 300]).prop_map(Value::from);
    (
        word_value::<TempBasalMetric>(),
        word_value::<CmpOp>(),
        quantity(),
        percent,
    )
        .prop_map(|(metric, operator, rate_value, percent_value)| {
            let value = if metric == "rate" {
                rate_value
            } else {
                percent_value
            };
            json!({ "metric": metric, "operator": operator, "value": value })
        })
        .boxed()
}

family!(
    temp_basal_compares_the_rate_or_the_percentage_and_is_false_without_a_temp,
    ConditionKind::TempBasal,
    leaf(temp_basal_payload()),
    temp_basal_model,
    [
        "true:rate",
        "false:rate",
        "true:percent",
        "false:percent",
        "false:none",
        "unknown:unscheduled"
    ]
);

// ---------------------------------------------------------------------------
// elapsed time since an anchor
// ---------------------------------------------------------------------------

fn age_model(c: &Case, anchor: &str, unit_seconds: i64) -> Verdict {
    let Some(at) = ago(&c.ctx, anchor) else {
        return unknown("absent");
    };
    passed(at).map_or_else(
        || unknown("ahead"),
        |p| {
            verdict(
                compared(c, Decimal::from(p) / Decimal::from(unit_seconds)),
                "measured",
            )
        },
    )
}

fn site_age_model(c: &Case) -> Verdict {
    age_model(c, "last_site_change_at", 3600)
}

fn sensor_age_model(c: &Case) -> Verdict {
    age_model(c, "last_sensor_start_at", 86_400)
}

family!(
    site_age_compares_hours_since_the_site_change,
    ConditionKind::SiteAge,
    leaf(tenths_compare_payload(0..=50)),
    site_age_model,
    ["true:measured", "false:measured", "unknown:absent"]
);

family!(
    sensor_age_compares_days_since_the_sensor_start,
    ConditionKind::SensorAge,
    leaf(tenths_compare_payload(0..=50)),
    sensor_age_model,
    ["true:measured", "false:measured", "unknown:absent"]
);

fn loop_model(c: &Case, anchor: &str) -> Verdict {
    if !flag(&c.ctx, "has_ever_aps_cycled") {
        return unknown("unguarded");
    }
    let Some(at) = ago(&c.ctx, anchor) else {
        return unknown("absent");
    };
    passed(at).map_or_else(
        || unknown("ahead"),
        |p| verdict(compared_minutes(c, "minutes", p), "measured"),
    )
}

fn compared_minutes(c: &Case, field: &str, passed_seconds: i64) -> Truth {
    compare(
        operator(c),
        Decimal::from(passed_seconds) / Decimal::from(60),
        operand(c, field),
    )
    .into()
}

fn loop_stale_model(c: &Case) -> Verdict {
    loop_model(c, "last_aps_cycle_at")
}

fn loop_enaction_stale_model(c: &Case) -> Verdict {
    loop_model(c, "last_aps_enacted_at")
}

family!(
    loop_stale_is_unknown_until_the_loop_has_cycled_and_then_measures_the_last_cycle,
    ConditionKind::LoopStale,
    leaf(whole_compare_payload("minutes", 0..=5)),
    loop_stale_model,
    [
        "true:measured",
        "false:measured",
        "unknown:absent",
        "unknown:unguarded"
    ]
);

family!(
    loop_enaction_stale_is_guarded_by_the_cycle_flag_and_measures_the_last_enactment,
    ConditionKind::LoopEnactionStale,
    leaf(whole_compare_payload("minutes", 0..=5)),
    loop_enaction_stale_model,
    [
        "true:measured",
        "false:measured",
        "unknown:absent",
        "unknown:unguarded"
    ]
);

/// No anchor is infinitely long ago: `>` and `>=` hold, and `<`, `<=` and `==` do not.
fn time_since_model(c: &Case, anchor: &str) -> Verdict {
    let Some(at) = ago(&c.ctx, anchor) else {
        return verdict(matches!(operator(c), ">" | ">="), "never");
    };
    passed(at).map_or_else(
        || unknown("ahead"),
        |p| verdict(compared_minutes(c, "minutes", p), "measured"),
    )
}

fn time_since_carb_model(c: &Case) -> Verdict {
    time_since_model(c, "last_carb_at")
}

fn time_since_bolus_model(c: &Case) -> Verdict {
    time_since_model(c, "last_bolus_at")
}

family!(
    time_since_last_carb_measures_minutes_and_reads_no_carb_as_infinitely_long_ago,
    ConditionKind::TimeSinceLastCarb,
    leaf(whole_compare_payload("minutes", 0..=5)),
    time_since_carb_model,
    [
        "true:measured",
        "false:measured",
        "true:never",
        "false:never"
    ]
);

family!(
    time_since_last_bolus_measures_minutes_and_reads_no_bolus_as_infinitely_long_ago,
    ConditionKind::TimeSinceLastBolus,
    leaf(whole_compare_payload("minutes", 0..=5)),
    time_since_bolus_model,
    [
        "true:measured",
        "false:measured",
        "true:never",
        "false:never"
    ]
);

// ---------------------------------------------------------------------------
// spans
// ---------------------------------------------------------------------------

fn held(p: Option<i64>, minutes: i64) -> Truth {
    p.map_or(Truth::Unknown, |p| (p >= minutes * 60).into())
}

fn is_active(c: &Case) -> bool {
    c.payload["is_active"].as_bool().unwrap()
}

fn for_minutes(c: &Case) -> Option<i64> {
    field(&c.payload, "for_minutes").and_then(Value::as_i64)
}

/// The leaf is true while the span's presence equals `is_active`; on the active side a
/// `for_minutes` also needs the span to have lasted that long.
fn active_for(c: &Case, started: Option<i64>) -> Verdict {
    match started {
        Some(at) if is_active(c) => match for_minutes(c) {
            None => verdict(true, "present"),
            Some(m) => match held(passed(at), m) {
                Truth::Unknown => unknown("ahead"),
                t => verdict(t, "lasting"),
            },
        },
        Some(_) => verdict(false, "present, wanted absent"),
        None => verdict(!is_active(c), "absent"),
    }
}

fn pump_suspended_model(c: &Case) -> Verdict {
    if !flag(&c.ctx, "has_ever_pump_snapshot") {
        return unknown("unguarded");
    }
    active_for(c, started_ago(&c.ctx, "active_pump_suspension"))
}

fn override_model(c: &Case) -> Verdict {
    active_for(c, started_ago(&c.ctx, "active_override"))
}

fn do_not_disturb_model(c: &Case) -> Verdict {
    active_for(c, started_ago(&c.ctx, "active_do_not_disturb"))
}

family!(
    pump_suspended_is_unknown_until_a_pump_has_reported_and_then_matches_the_suspension,
    ConditionKind::PumpSuspended,
    leaf(active_payload()),
    pump_suspended_model,
    [
        "true:present",
        "true:lasting",
        "false:lasting",
        "false:present, wanted absent",
        "true:absent",
        "false:absent",
        "unknown:unguarded"
    ]
);

family!(
    override_active_matches_the_override_with_no_guard,
    ConditionKind::OverrideActive,
    leaf(active_payload()),
    override_model,
    [
        "true:present",
        "true:lasting",
        "false:lasting",
        "false:present, wanted absent",
        "true:absent",
        "false:absent"
    ]
);

family!(
    do_not_disturb_matches_the_snapshot_with_no_guard,
    ConditionKind::DoNotDisturb,
    leaf(active_payload()),
    do_not_disturb_model,
    [
        "true:present",
        "true:lasting",
        "false:lasting",
        "false:present, wanted absent",
        "true:absent",
        "false:absent"
    ]
);

fn pick_alert_id() -> BoxedStrategy<Value> {
    select(ALERT_IDS.to_vec())
        .prop_map(|id| Value::from(id.to_string()))
        .boxed()
}

fn alert_state_model(c: &Case) -> Verdict {
    let id = c.payload["alert_id"].as_str().unwrap();
    let snapshot = field(&c.ctx, "active_alerts")
        .and_then(Value::as_array)
        .and_then(|alerts| alerts.iter().find(|a| a["alert_id"] == id));
    let Some(snapshot) = snapshot else {
        return verdict(false, "no snapshot");
    };
    let firing = snapshot["state"]
        .as_str()
        .unwrap()
        .eq_ignore_ascii_case("firing");
    let triggered = ago_of_instant(&snapshot["triggered_at"]);
    let acknowledged = field(snapshot, "acknowledged_at").map(ago_of_instant);
    let anchor = match c.payload["state"].as_str().unwrap() {
        "firing" => firing.then_some(triggered),
        "unacknowledged" => (firing && acknowledged.is_none()).then_some(triggered),
        _ => acknowledged,
    };
    match (anchor, for_minutes(c)) {
        (None, _) => verdict(false, "state"),
        (Some(_), None) => verdict(true, "state"),
        (Some(at), Some(m)) => match held(passed(at), m) {
            Truth::Unknown => unknown("ahead"),
            t => verdict(t, "lasting"),
        },
    }
}

family!(
    alert_state_matches_the_other_alert_s_state_for_as_long_as_asked,
    ConditionKind::AlertState,
    leaf(
        (
            pick_alert_id(),
            word_value::<AlertStateKind>(),
            option::of(-1..=5i32)
        )
            .prop_map(|(alert_id, state, for_minutes)| {
                object(vec![
                    ("alert_id", Some(alert_id)),
                    ("state", Some(state)),
                    ("for_minutes", for_minutes.map(Value::from)),
                ])
            })
            .boxed()
    ),
    alert_state_model,
    [
        "true:state",
        "false:state",
        "true:lasting",
        "false:lasting",
        "false:no snapshot"
    ]
);

/// §3 table over the set of active modes: none, one of the named mode (whatever else is
/// active), only other known modes, or none of the named mode with one the crate has no member
/// for (unknown wherever the answer depends on the mode).
fn pump_state_model(c: &Case) -> Verdict {
    let wanted = c.payload["mode"].as_str().unwrap();
    let active = is_active(c);
    let waits = for_minutes(c).is_some();
    let entries: Vec<(&str, i64)> = field(&c.ctx, "active_pump_modes")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .map(|m| {
            (
                m["mode"].as_str().unwrap(),
                ago_of_instant(&m["started_at"]),
            )
        })
        .collect();
    let unrecognised_present = entries
        .iter()
        .any(|(mode, _)| PumpMode::from_name(mode).is_none());
    let named: Vec<i64> = entries
        .iter()
        .filter(|(mode, _)| *mode == wanted)
        .map(|(_, ago)| *ago)
        .collect();
    if let Some(&latest_start) = named.iter().min() {
        let repeated = match (named.len() > 1, unrecognised_present, entries.len() > 1) {
            (true, _, _) => " repeated",
            (_, true, _) => " beside unrecognised",
            (_, _, true) => " beside others",
            _ => "",
        };
        if !active {
            return verdict(false, &format!("named, wanted absent{repeated}"));
        }
        return match for_minutes(c) {
            None => verdict(true, &format!("named{repeated}")),
            Some(m) => match held(passed(latest_start), m) {
                Truth::Unknown => unknown("ahead"),
                t => verdict(
                    t,
                    if named.len() > 1 {
                        "named, lasting repeated"
                    } else {
                        "named, lasting"
                    },
                ),
            },
        };
    }
    if let Some(earliest_start) = entries
        .iter()
        .filter(|(mode, _)| PumpMode::from_name(mode).is_none())
        .map(|(_, ago)| *ago)
        .max()
    {
        let beside = if entries
            .iter()
            .any(|(mode, _)| PumpMode::from_name(mode).is_some())
        {
            " beside others"
        } else {
            ""
        };
        return match (active, waits) {
            (false, _) | (true, false) => unknown(&format!("unrecognised{beside}")),
            (true, true) => match held(passed(earliest_start), for_minutes(c).unwrap()) {
                Truth::False => verdict(false, "unrecognised, not yet"),
                _ => unknown(&format!("unrecognised{beside}")),
            },
        };
    }
    verdict(
        !active,
        if entries.is_empty() {
            "no state"
        } else {
            "other"
        },
    )
}

fn pump_state_case() -> BoxedStrategy<Case> {
    (
        full_context(),
        word_value::<PumpMode>(),
        any::<bool>(),
        any::<bool>(),
        option::of(select(vec![0i32, 1, 5])),
        any::<prop::sample::Index>(),
        option::of(age_in(60)),
        option::weighted(0.85, pump_modes(1)),
    )
        .prop_map(
            |(mut ctx, random, from_context, is_active, for_minutes, index, repeat, modes)| {
                if let (Some(modes), Some(fields)) = (modes, ctx.as_object_mut()) {
                    fields.insert("active_pump_modes".into(), modes);
                }
                if let (Some(ago), Some(modes)) = (
                    repeat,
                    ctx.get_mut("active_pump_modes")
                        .and_then(Value::as_array_mut),
                ) && let Some(first) = modes.first().cloned()
                {
                    modes.push(json!({
                        "mode": first["mode"],
                        "started_at": (base() - TimeDelta::seconds(ago)).to_rfc3339(),
                    }));
                }
                let present: Vec<Value> = field(&ctx, "active_pump_modes")
                    .and_then(Value::as_array)
                    .into_iter()
                    .flatten()
                    .map(|m| m["mode"].clone())
                    .filter(|m| PumpMode::from_name(m.as_str().unwrap()).is_some())
                    .collect();
                let mode = if from_context && !present.is_empty() {
                    present[index.index(present.len())].clone()
                } else {
                    random
                };
                let payload = object(vec![
                    ("mode", Some(mode)),
                    ("is_active", Some(is_active.into())),
                    ("for_minutes", for_minutes.map(Value::from)),
                ]);
                case(ctx, payload)
            },
        )
        .boxed()
}

family!(
    pump_state_follows_the_table_of_what_the_pump_is_in,
    ConditionKind::PumpState,
    pump_state_case(),
    pump_state_model,
    [
        "true:no state",
        "false:no state",
        "true:named",
        "true:named, lasting",
        "false:named, lasting",
        "false:named, wanted absent",
        "true:named beside others",
        "true:named beside unrecognised",
        "false:named, wanted absent beside others",
        "false:named, wanted absent beside unrecognised",
        "true:named repeated",
        "false:named, lasting repeated",
        "true:other",
        "false:other",
        "unknown:unrecognised",
        "unknown:unrecognised beside others",
        "false:unrecognised, not yet"
    ]
);

fn recognised_spans(ctx: &Value) -> Vec<(String, Option<String>)> {
    field(ctx, "active_state_spans")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .filter(|s| StateSpanCategory::from_name(s["category"].as_str().unwrap()).is_some())
        .map(|s| {
            (
                s["category"].as_str().unwrap().to_owned(),
                field(s, "state").map(|v| v.as_str().unwrap().to_owned()),
            )
        })
        .collect()
}

fn state_span_model(c: &Case) -> Verdict {
    let category = c.payload["category"].as_str().unwrap().to_owned();
    let state = field(&c.payload, "state").map(|v| v.as_str().unwrap().to_owned());
    let started = field(&c.ctx, "active_state_spans")
        .and_then(Value::as_array)
        .into_iter()
        .flatten()
        .rev()
        .find(|s| {
            s["category"] == category.as_str()
                && field(s, "state").map(|v| v.as_str().unwrap().to_owned()) == state
        })
        .map(|s| ago_of_instant(&s["started_at"]));
    active_for(c, started)
}

fn state_span_case() -> BoxedStrategy<Case> {
    let fresh_key = (
        select(
            StateSpanCategory::ALL
                .iter()
                .filter(|c| **c != StateSpanCategory::PumpMode)
                .map(|c| c.name().to_owned())
                .collect::<Vec<_>>(),
        ),
        option::of(select(vec!["a".to_owned(), "b".to_owned()])),
    );
    (
        full_context(),
        fresh_key,
        any::<bool>(),
        any::<bool>(),
        option::of(-1..=5i32),
        any::<prop::sample::Index>(),
    )
        .prop_map(
            |(ctx, random, from_context, is_active, for_minutes, index)| {
                let present = recognised_spans(&ctx);
                let (category, state) = if from_context && !present.is_empty() {
                    present[index.index(present.len())].clone()
                } else {
                    random
                };
                let payload = object(vec![
                    ("category", Some(category.into())),
                    ("state", state.map(Value::from)),
                    ("is_active", Some(is_active.into())),
                    ("for_minutes", for_minutes.map(Value::from)),
                ]);
                case(ctx, payload)
            },
        )
        .boxed()
}

family!(
    state_span_active_matches_the_exact_category_and_state_key_and_ignores_an_unrecognised_category,
    ConditionKind::StateSpanActive,
    state_span_case(),
    state_span_model,
    [
        "true:present",
        "true:lasting",
        "false:lasting",
        "false:present, wanted absent",
        "true:absent",
        "false:absent"
    ]
);

fn sensor_state_model(c: &Case) -> Verdict {
    let Some(snapshot) = sensor_state_now(&c.ctx) else {
        let listed = c.payload["states"].as_array().unwrap();
        return if field(&c.ctx, "latest_value").is_some() && listed.iter().all(|s| s == "no_value")
        {
            verdict(false, "a value, so not no_value")
        } else {
            unknown("absent")
        };
    };
    let state = snapshot["state"].as_str().unwrap();
    if SensorState::from_name(state).is_none() {
        return unknown("unrecognised");
    }
    if !c.payload["states"]
        .as_array()
        .unwrap()
        .contains(&snapshot["state"])
    {
        return verdict(false, "other state");
    }
    match (field(snapshot, "since").map(ago_of_instant), for_minutes(c)) {
        (_, None) => verdict(true, "listed"),
        (None, Some(_)) => unknown("no start"),
        (Some(at), Some(m)) => match held(passed(at), m) {
            Truth::Unknown => unknown("ahead"),
            t => verdict(t, "lasting"),
        },
    }
}

fn sensor_state_case() -> BoxedStrategy<Case> {
    (
        full_context(),
        prop_oneof![5 => words::<SensorState>(), 1 => Just(json!(["no_value"]))],
        option::weighted(0.75, -1..=5i32),
        option::weighted(0.5, full_context::sensor_state()),
    )
        .prop_map(|(mut ctx, states, for_minutes, sensor)| {
            if let Some(sensor) = sensor {
                ctx["sensor_state"] = sensor;
            }
            let payload = object(vec![
                ("states", Some(states)),
                ("for_minutes", for_minutes.map(Value::from)),
            ]);
            case(ctx, payload)
        })
        .boxed()
}

family!(
    sensor_state_is_set_membership_held_from_its_start_and_unknown_without_a_known_state,
    ConditionKind::SensorState,
    sensor_state_case(),
    sensor_state_model,
    [
        "true:listed",
        "true:lasting",
        "false:lasting",
        "false:other state",
        "unknown:absent",
        "unknown:unrecognised",
        "unknown:no start",
        "false:a value, so not no_value"
    ]
);

fn tracker_model(c: &Case) -> Verdict {
    let id = c.payload["tracker_definition_id"].as_str().unwrap();
    let instance = field(&c.ctx, "active_trackers")
        .and_then(Value::as_array)
        .and_then(|all| all.iter().find(|t| t["tracker_definition_id"] == id));
    let Some(instance) = instance else {
        return verdict(false, "none");
    };
    let reference = ago_of_instant(&instance["reference_at"]);
    let scheduled = instance["scheduled"].as_bool().unwrap();
    let minutes = whole(c, "minutes");
    if !scheduled && let Some(recorded) = field(instance, "recorded_at").map(ago_of_instant) {
        let due = reference - minutes * 60;
        let already = match operator(c) {
            ">=" => due >= recorded,
            ">" => due > recorded,
            _ => false,
        };
        if already {
            return verdict(false, "held back");
        }
    }
    let elapsed = if scheduled {
        Some(reference)
    } else {
        passed(reference)
    };
    elapsed.map_or_else(
        || unknown("ahead"),
        |p| {
            verdict(
                compared_minutes(c, "minutes", p),
                if scheduled { "event" } else { "duration" },
            )
        },
    )
}

/// A duration instance recorded at its threshold's due time, or a second either side of it,
/// about a third of the time.
fn tracker_case() -> BoxedStrategy<Case> {
    (
        full_context(),
        select(TRACKER_IDS.to_vec()),
        word_value::<CmpOp>(),
        -5..=5i32,
        option::weighted(0.4, -1..=1i64),
    )
        .prop_map(|(mut ctx, id, operator, minutes, recorded_beside_due)| {
            let id = id.to_string();
            let instance = ctx
                .get_mut("active_trackers")
                .and_then(Value::as_array_mut)
                .and_then(|all| {
                    all.iter_mut()
                        .find(|t| t["tracker_definition_id"] == id.as_str())
                });
            if let (Some(instance), Some(beside)) = (instance, recorded_beside_due) {
                let due = ago_of_instant(&instance["reference_at"]) - i64::from(minutes) * 60;
                instance["recorded_at"] = Value::from(
                    (base() - TimeDelta::seconds(due) + TimeDelta::seconds(beside)).to_rfc3339(),
                );
            }
            let payload = json!({
                "tracker_definition_id": id,
                "operator": operator,
                "minutes": minutes
            });
            case(ctx, payload)
        })
        .boxed()
}

family!(
    tracker_age_is_false_without_an_instance_and_for_a_threshold_held_when_a_duration_was_recorded,
    ConditionKind::TrackerAge,
    tracker_case(),
    tracker_model,
    [
        "false:none",
        "true:event",
        "false:event",
        "true:duration",
        "false:duration",
        "false:held back"
    ]
);

fn sleep_model(c: &Case) -> Verdict {
    verdict(flag(&c.ctx, "sleep_session_active") == is_active(c), "flag")
}

family!(
    sleep_session_active_is_true_while_the_flag_equals_is_active,
    ConditionKind::SleepSessionActive,
    leaf(
        any::<bool>()
            .prop_map(|a| json!({ "is_active": a }))
            .boxed()
    ),
    sleep_model,
    ["true:flag", "false:flag"]
);

// ---------------------------------------------------------------------------
// the clock and the tenant's zone
// ---------------------------------------------------------------------------

fn zone_of(name: &str) -> Option<Tz> {
    name.parse().ok().or_else(|| {
        chrono_tz::TZ_VARIANTS
            .iter()
            .find(|tz| tz.name().eq_ignore_ascii_case(name))
            .copied()
    })
}

fn tenant_zone(ctx: &Value) -> Option<Tz> {
    field(ctx, "tenant_time_zone_id").and_then(|z| zone_of(z.as_str().unwrap()))
}

fn minute_of_day(at: DateTime<Tz>) -> u32 {
    at.hour() * 60 + at.minute()
}

fn clock(minutes: u32) -> Value {
    Value::from(format!("{:02}:{:02}", minutes / 60, minutes % 60))
}

fn time_of_day_case() -> BoxedStrategy<Case> {
    (
        full_context(),
        option::of(select(vec!["UTC", "Australia/Sydney", "America/New_York"])),
        0..5u32,
        0..5u32,
    )
        .prop_map(|(ctx, rule_zone, before, after)| {
            let tz = rule_zone
                .and_then(zone_of)
                .or_else(|| tenant_zone(&ctx))
                .unwrap_or(Tz::UTC);
            let now = minute_of_day(base().with_timezone(&tz));
            let from = (now + 1438 + before) % 1440;
            let mut to = (now + after) % 1440;
            if to == from {
                to = (to + 1) % 1440;
            }
            let payload = object(vec![
                ("from", Some(clock(from))),
                ("to", Some(clock(to))),
                ("timezone", rule_zone.map(Value::from)),
            ]);
            case(ctx, payload)
        })
        .boxed()
}

fn time_of_day_model(c: &Case) -> Verdict {
    let rule = field(&c.payload, "timezone").map(|z| zone_of(z.as_str().unwrap()).unwrap());
    let (tz, source) = match (rule, tenant_zone(&c.ctx)) {
        (Some(tz), _) => (tz, "rule zone"),
        (None, Some(tz)) => (tz, "tenant zone"),
        (None, None) => (Tz::UTC, "utc"),
    };
    let now = minute_of_day(base().with_timezone(&tz));
    let minutes = |name: &str| {
        let text = c.payload[name].as_str().unwrap();
        let (h, m) = text.split_once(':').unwrap();
        h.parse::<u32>().unwrap() * 60 + m.parse::<u32>().unwrap()
    };
    let (from, to) = (minutes("from"), minutes("to"));
    let inside = if from < to {
        now >= from && now < to
    } else {
        now >= from || now < to
    };
    verdict(inside, source)
}

family!(
    time_of_day_is_a_half_open_window_in_the_rule_zone_else_the_tenant_zone_else_utc,
    ConditionKind::TimeOfDay,
    time_of_day_case(),
    time_of_day_model,
    [
        "true:rule zone",
        "false:rule zone",
        "true:tenant zone",
        "false:tenant zone",
        "true:utc",
        "false:utc"
    ]
);

fn day_of_week_model(c: &Case) -> Verdict {
    let tz = tenant_zone(&c.ctx).unwrap_or(Tz::UTC);
    let today = match base().with_timezone(&tz).weekday() {
        Weekday::Sun => "sunday",
        Weekday::Mon => "monday",
        Weekday::Tue => "tuesday",
        Weekday::Wed => "wednesday",
        Weekday::Thu => "thursday",
        Weekday::Fri => "friday",
        Weekday::Sat => "saturday",
    };
    let listed = c.payload["days"]
        .as_array()
        .unwrap()
        .contains(&Value::from(today));
    let source = if tenant_zone(&c.ctx).is_some() {
        "tenant zone"
    } else {
        "utc"
    };
    verdict(listed, source)
}

family!(
    day_of_week_is_the_weekday_in_the_tenant_zone_else_utc,
    ConditionKind::DayOfWeek,
    leaf(
        vec(word::<DayOfWeek>(), 1..=4)
            .prop_map(|days| json!({ "days": days }))
            .boxed()
    ),
    day_of_week_model,
    [
        "true:tenant zone",
        "false:tenant zone",
        "true:utc",
        "false:utc"
    ]
);

// ---------------------------------------------------------------------------
// the generator fills every field
// ---------------------------------------------------------------------------

/// Whether each field of a context holds something, named. Destructures the context with no
/// `..`, so a field added to `SensorContext` stops this compiling until it is listed here, and
/// `the_full_context_fills_every_field_and_leaves_each_empty_as_often` then holds the generator
/// to filling it.
fn presence(ctx: &SensorContext) -> Vec<(&'static str, bool)> {
    let SensorContext {
        latest_value,
        latest_timestamp,
        latest_received_at,
        trend_rate,
        last_reading_at,
        trend_bucket,
        iob_units,
        cob_grams,
        reservoir_units,
        reservoir_is_lower_bound,
        last_site_change_at,
        last_sensor_start_at,
        predictions,
        prediction_curves,
        aps_determination,
        active_alerts,
        last_aps_cycle_at,
        last_aps_enacted_at,
        pump_battery_percent,
        active_temp_basal,
        uploader_battery_percent,
        transmitter_voltage_a,
        transmitter_voltage_b,
        transmitter_resistance,
        transmitter_days,
        active_override,
        active_pump_suspension,
        sensitivity_ratio,
        active_do_not_disturb,
        has_ever_aps_cycled,
        has_ever_pump_snapshot,
        has_ever_uploader_snapshot,
        has_ever_aps_sensitivity,
        glucose_bucket,
        target_low,
        target_high,
        last_carb_at,
        last_bolus_at,
        tenant_time_zone_id,
        active_pump_modes,
        active_state_spans,
        active_trackers,
        sleep_session_active,
        sensor_state,
    } = ctx;
    vec![
        ("latest_value", latest_value.is_some()),
        ("latest_timestamp", latest_timestamp.is_some()),
        ("latest_received_at", latest_received_at.is_some()),
        ("trend_rate", trend_rate.is_some()),
        ("last_reading_at", last_reading_at.is_some()),
        ("trend_bucket", trend_bucket.is_some()),
        ("iob_units", iob_units.is_some()),
        ("cob_grams", cob_grams.is_some()),
        ("reservoir_units", reservoir_units.is_some()),
        ("reservoir_is_lower_bound", *reservoir_is_lower_bound),
        ("last_site_change_at", last_site_change_at.is_some()),
        ("last_sensor_start_at", last_sensor_start_at.is_some()),
        ("predictions", !predictions.is_empty()),
        ("prediction_curves", !prediction_curves.is_empty()),
        ("aps_determination", aps_determination.is_some()),
        ("active_alerts", !active_alerts.is_empty()),
        ("last_aps_cycle_at", last_aps_cycle_at.is_some()),
        ("last_aps_enacted_at", last_aps_enacted_at.is_some()),
        ("pump_battery_percent", pump_battery_percent.is_some()),
        ("active_temp_basal", active_temp_basal.is_some()),
        (
            "uploader_battery_percent",
            uploader_battery_percent.is_some(),
        ),
        ("transmitter_voltage_a", transmitter_voltage_a.is_some()),
        ("transmitter_voltage_b", transmitter_voltage_b.is_some()),
        ("transmitter_resistance", transmitter_resistance.is_some()),
        ("transmitter_days", transmitter_days.is_some()),
        ("active_override", active_override.is_some()),
        ("active_pump_suspension", active_pump_suspension.is_some()),
        ("sensitivity_ratio", sensitivity_ratio.is_some()),
        ("active_do_not_disturb", active_do_not_disturb.is_some()),
        ("has_ever_aps_cycled", *has_ever_aps_cycled),
        ("has_ever_pump_snapshot", *has_ever_pump_snapshot),
        ("has_ever_uploader_snapshot", *has_ever_uploader_snapshot),
        ("has_ever_aps_sensitivity", *has_ever_aps_sensitivity),
        ("glucose_bucket", glucose_bucket.is_some()),
        ("target_low", target_low.is_some()),
        ("target_high", target_high.is_some()),
        ("last_carb_at", last_carb_at.is_some()),
        ("last_bolus_at", last_bolus_at.is_some()),
        ("tenant_time_zone_id", tenant_time_zone_id.is_some()),
        ("active_pump_modes", !active_pump_modes.is_empty()),
        ("active_state_spans", !active_state_spans.is_empty()),
        ("active_trackers", !active_trackers.is_empty()),
        ("sleep_session_active", *sleep_session_active),
        ("sensor_state", sensor_state.is_some()),
    ]
}

#[test]
fn the_full_context_fills_every_field_and_leaves_each_empty_as_often() {
    let mut runner = TestRunner::deterministic();
    let strategy = full_context();
    let mut filled: BTreeMap<&'static str, usize> = BTreeMap::new();
    let mut fields = 0;
    for _ in 0..SAMPLES {
        let ctx: SensorContext =
            serde_json::from_value(strategy.new_tree(&mut runner).unwrap().current()).unwrap();
        let seen = presence(&ctx);
        fields = seen.len();
        for (name, present) in seen {
            *filled.entry(name).or_default() += usize::from(present);
        }
    }
    println!("{fields} fields, filled in of {SAMPLES}: {filled:?}");
    assert_eq!(filled.len(), fields, "a field is named twice");
    for (name, count) in filled {
        assert!(
            (SAMPLES / 5..=SAMPLES - SAMPLES / 5).contains(&count),
            "{name} is filled in {count} of {SAMPLES} contexts"
        );
    }
}

/// A forecast leaf that is true under a `not` argues against the rule, so it never names what the
/// rule met; the same leaf outside the `not` does.
#[test]
fn a_true_leaf_under_not_names_nothing() {
    let ctx: SensorContext = serde_json::from_value(json!({
        "latest_value": 60,
        "latest_timestamp": base().to_rfc3339(),
        "predictions": [{ "offset_minutes": 5, "mgdl": 185 }],
    }))
    .expect("a context");
    let high = json!({ "type": "predicted", "predicted": { "operator": ">", "value": 180, "within_minutes": 30 } });
    let low = json!({ "type": "threshold", "threshold": { "direction": "below", "value": 70 } });
    let met = |conditions: Value| {
        let wire: WireRule = serde_json::from_value(json!({
            "id": Uuid::nil(),
            "condition_type": "composite",
            "condition_params": { "operator": "or", "conditions": conditions },
        }))
        .expect("a rule");
        let rule = Rule::try_from(wire).expect("a rule");
        let outcome = evaluate_rule(&rule, &ctx, base(), &mut EngineState::new(), false);
        assert!(
            outcome
                .evaluation
                .as_ref()
                .expect("evaluated")
                .root
                .is_true()
        );
        outcome.to_json()["met_by"].clone()
    };

    assert_eq!(
        met(json!([low, { "type": "not", "not": { "child": high } }])),
        Value::Null
    );
    assert_eq!(
        met(json!([low, high])),
        json!([{ "leaf_id": 1, "kind": "predicted", "curve": "default", "offset_minutes": 5, "value": 185 }])
    );
}
