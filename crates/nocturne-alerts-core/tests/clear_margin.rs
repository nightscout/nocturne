//! A comparison leaf's clear margin (engine-semantics.md §3): read on the
//! eleven value leaves, applied only while the rule's excursion is open, and
//! checked against oracles written from the definition, not from the engine's
//! helper.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code"
)]

use chrono::{DateTime, TimeDelta, TimeZone, Utc};
use proptest::prelude::*;
use proptest::strategy::ValueTree;
use proptest::test_runner::TestRunner;
use rust_decimal::Decimal;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::context::{PredictedPoint, SensorContext, TempBasalSnapshot};
use nocturne_alerts_core::engine::{
    EngineState, Evaluation, OnUnknown, Rule, WireRule, evaluate_rule,
};
use nocturne_alerts_core::excursion::{TrackerState, TrackerStateKind, TransitionType};
use nocturne_alerts_core::model::{ConditionKind, Reason, parse_payload};
use nocturne_alerts_core::truth::Truth;

const RULE_ID: Uuid = Uuid::from_u128(1);

fn base() -> DateTime<Utc> {
    Utc.with_ymd_and_hms(2026, 1, 5, 12, 0, 0).unwrap()
}

fn at(minutes: i64) -> DateTime<Utc> {
    base() + TimeDelta::minutes(minutes)
}

fn d(s: &str) -> Decimal {
    s.parse().unwrap()
}

fn rule(kind: &str, params: Value) -> Rule {
    let wire: WireRule = serde_json::from_value(json!({
        "id": RULE_ID,
        "condition_type": kind,
        "condition_params": params,
    }))
    .unwrap();
    Rule::try_from(wire).unwrap()
}

fn node(kind: &str, payload: Value) -> Value {
    json!({ "type": kind, kind: payload })
}

/// `threshold below 70` with a margin of 5: closed, true below 70; open,
/// true below 75.
fn low_with_margin() -> Value {
    json!({ "direction": "below", "value": 70, "clear_margin": 5 })
}

fn glucose(value: &str, now: DateTime<Utc>) -> SensorContext {
    SensorContext {
        latest_value: Some(d(value)),
        latest_timestamp: Some(now),
        last_reading_at: Some(now),
        ..Default::default()
    }
}

fn step(
    rule: &Rule,
    ctx: &SensorContext,
    now: DateTime<Utc>,
    state: &mut EngineState,
) -> Evaluation {
    evaluate_rule(rule, ctx, now, state, true)
        .evaluation
        .expect("the rule is evaluable")
}

fn tracker(kind: TrackerStateKind, excursion: Option<u32>) -> TrackerState {
    TrackerState {
        state: kind,
        active_excursion: excursion,
        last_excursion: excursion.unwrap_or(1),
        updated_at: base() - TimeDelta::minutes(1),
        hysteresis_started_at: (kind == TrackerStateKind::Hysteresis)
            .then(|| base() - TimeDelta::minutes(1)),
        awaiting_rearm: false,
        held_since: None,
    }
}

fn restored(t: TrackerState) -> EngineState {
    let mut state = EngineState::new();
    state.tracker.restore_state(RULE_ID, t);
    state
}

fn open() -> EngineState {
    restored(tracker(TrackerStateKind::Active, Some(1)))
}

fn root_at(rule: &Rule, value: &str, state: &mut EngineState) -> Truth {
    step(rule, &glucose(value, base()), base(), state).root
}

// ---------------------------------------------------------------------------
// Reading
// ---------------------------------------------------------------------------

/// Each kind that takes a margin, with a payload carrying one.
fn margined_payloads() -> Vec<(ConditionKind, Value)> {
    let cmp = |op: &str| json!({ "operator": op, "value": 10, "clear_margin": 2 });
    vec![
        (ConditionKind::Threshold, low_with_margin()),
        (
            ConditionKind::RateOfChange,
            json!({ "direction": "falling", "rate": 2, "clear_margin": 1 }),
        ),
        (
            ConditionKind::Predicted,
            json!({ "operator": "<", "value": 70, "within_minutes": 30, "clear_margin": 5 }),
        ),
        (ConditionKind::Iob, cmp(">")),
        (ConditionKind::Cob, cmp(">=")),
        (ConditionKind::Reservoir, cmp("<")),
        (ConditionKind::PumpBattery, cmp("<=")),
        (ConditionKind::UploaderBattery, cmp("<")),
        (ConditionKind::SensitivityRatio, cmp(">")),
        (
            ConditionKind::TempBasal,
            json!({ "metric": "rate", "operator": ">", "value": 1, "clear_margin": 0.5 }),
        ),
        (
            ConditionKind::TransmitterBattery,
            json!({ "metric": "voltage_a", "operator": "<", "value": 290, "clear_margin": 10 }),
        ),
    ]
}

#[test]
fn every_comparison_leaf_reads_a_margin() {
    let payloads = margined_payloads();
    assert_eq!(payloads.len(), 11);
    for (kind, payload) in payloads {
        let parsed = parse_payload(kind, &payload).unwrap_or_else(|e| panic!("{kind:?}: {e}"));
        let margin = parsed.margined().and_then(|m| m.clear_margin());
        assert_eq!(
            margin.map(|m| m.get()),
            Some(Decimal::from_str_exact(&payload["clear_margin"].to_string()).unwrap()),
            "{kind:?}"
        );
    }
}

/// An engine reads a member its kind does not declare as an unknown field,
/// so the tree is not evaluated (§1.4). The elapsed-time leaves declare no
/// margin, and an engine from before the margin declares it nowhere.
#[test]
fn a_leaf_without_a_margin_does_not_read_one() {
    let elapsed = [
        (
            ConditionKind::SiteAge,
            json!({ "operator": ">=", "value": 72 }),
        ),
        (
            ConditionKind::SensorAge,
            json!({ "operator": ">=", "value": 10 }),
        ),
        (
            ConditionKind::Staleness,
            json!({ "operator": ">=", "value": 15 }),
        ),
        (
            ConditionKind::LoopStale,
            json!({ "operator": ">", "minutes": 15 }),
        ),
        (
            ConditionKind::LoopEnactionStale,
            json!({ "operator": ">", "minutes": 15 }),
        ),
        (
            ConditionKind::TimeSinceLastCarb,
            json!({ "operator": ">", "minutes": 15 }),
        ),
        (
            ConditionKind::TimeSinceLastBolus,
            json!({ "operator": ">", "minutes": 15 }),
        ),
        (
            ConditionKind::TrackerAge,
            json!({ "tracker_definition_id": Uuid::from_u128(9), "operator": ">=", "minutes": 60 }),
        ),
        (ConditionKind::SignalLoss, json!({ "timeout_minutes": 20 })),
        (
            ConditionKind::PumpSuspended,
            json!({ "is_active": true, "for_minutes": 20 }),
        ),
    ];
    for (kind, mut payload) in elapsed {
        assert!(parse_payload(kind, &payload).is_ok(), "{kind:?} without");
        payload["clear_margin"] = json!(5);
        let issues = parse_payload(kind, &payload).unwrap_err().0;
        assert_eq!(
            issues.iter().map(|i| i.reason).collect::<Vec<_>>(),
            vec![Reason::UnknownField],
            "{kind:?}"
        );
    }
}

#[test]
fn a_margin_is_invalid_as_a_value_is_and_when_negative() {
    let reason = |margin: Value| {
        let mut payload = json!({ "operator": ">", "value": 2 });
        payload["clear_margin"] = margin;
        parse_payload(ConditionKind::Iob, &payload).err().map(|e| {
            e.0.iter()
                .map(|i| (i.reason.code(), i.reason.field()))
                .collect::<Vec<_>>()
        })
    };
    assert_eq!(
        reason(json!(-1)),
        Some(vec![("margin_negative", Some("clear_margin"))])
    );
    assert_eq!(
        reason(json!(-0.0001)),
        Some(vec![("margin_negative", Some("clear_margin"))])
    );
    assert_eq!(
        reason(json!("5")),
        Some(vec![("invalid_field", Some("clear_margin"))])
    );
    assert_eq!(
        reason(serde_json::from_str("1e40").unwrap()),
        Some(vec![("invalid_field", Some("clear_margin"))])
    );
    assert_eq!(reason(json!(0)), None);
    assert_eq!(reason(json!(-0)), None);
    assert_eq!(reason(Value::Null), None);
}

#[test]
fn a_margin_on_an_equality_is_invalid_and_zero_is_none() {
    let issues = |margin: i64| {
        parse_payload(
            ConditionKind::Iob,
            &json!({ "operator": "==", "value": 2, "clear_margin": margin }),
        )
        .err()
        .map(|e| e.0.iter().map(|i| i.reason).collect::<Vec<_>>())
    };
    assert_eq!(issues(1), Some(vec![Reason::MarginOnEquality]));
    assert_eq!(issues(0), None);
}

#[test]
fn a_margin_serialises_exactly_and_only_when_present() {
    let without = parse_payload(
        ConditionKind::Threshold,
        &json!({ "direction": "below", "value": 70 }),
    )
    .unwrap();
    assert_eq!(
        serde_json::to_value(&without).unwrap(),
        json!({ "direction": "below", "value": 70 })
    );
    let with: Value =
        serde_json::from_str(r#"{ "direction": "below", "value": 70, "clear_margin": 5.50 }"#)
            .unwrap();
    let parsed = parse_payload(ConditionKind::Threshold, &with).unwrap();
    assert_eq!(
        serde_json::to_string(&parsed).unwrap(),
        r#"{"direction":"below","value":70,"clear_margin":5.50}"#
    );
}

// ---------------------------------------------------------------------------
// Open, and what each tracker state reads as
// ---------------------------------------------------------------------------

#[test]
fn closed_the_leaf_compares_against_its_plain_limit() {
    let r = rule("threshold", low_with_margin());
    assert_eq!(root_at(&r, "72", &mut EngineState::new()), Truth::False);
    assert_eq!(root_at(&r, "69.9", &mut EngineState::new()), Truth::True);
}

#[test]
fn open_the_leaf_compares_against_its_widened_limit_and_stays_strict() {
    let r = rule("threshold", low_with_margin());
    assert_eq!(root_at(&r, "72", &mut open()), Truth::True);
    assert_eq!(root_at(&r, "74.99", &mut open()), Truth::True);
    assert_eq!(root_at(&r, "75", &mut open()), Truth::False);
}

#[test]
fn hysteresis_is_open_and_a_value_inside_the_margin_resumes() {
    let r = rule("threshold", low_with_margin());
    let mut state = restored(tracker(TrackerStateKind::Hysteresis, Some(1)));
    let e = step(&r, &glucose("72", base()), base(), &mut state);
    assert_eq!(e.root, Truth::True);
    assert_eq!(e.transition.kind, TransitionType::HysteresisResumed);
}

#[test]
fn idle_and_never_evaluated_are_closed() {
    let r = rule("threshold", low_with_margin());
    let mut idle = restored(tracker(TrackerStateKind::Idle, None));
    let e = step(&r, &glucose("72", base()), base(), &mut idle);
    assert_eq!(
        (e.root, e.transition.kind),
        (Truth::False, TransitionType::None)
    );
    let e = step(&r, &glucose("72", base()), base(), &mut EngineState::new());
    assert_eq!(
        (e.root, e.transition.kind),
        (Truth::False, TransitionType::None)
    );
}

#[test]
fn awaiting_rearm_is_closed_so_a_value_inside_the_margin_rearms() {
    let mut r = rule("threshold", low_with_margin());
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(node("iob", json!({ "operator": ">", "value": 1 })));
    let mut state = restored(TrackerState {
        awaiting_rearm: true,
        ..tracker(TrackerStateKind::Idle, None)
    });
    let ctx = SensorContext {
        iob_units: Some(d("2")),
        ..glucose("72", base())
    };
    let e = step(&r, &ctx, base(), &mut state);
    assert_eq!(e.root, Truth::False);
    assert!(!e.tracker.unwrap().awaiting_rearm);
}

#[test]
fn the_evaluation_that_opens_reads_closed_and_the_next_reads_open() {
    let r = rule("threshold", low_with_margin());
    let mut state = EngineState::new();
    let opened = step(&r, &glucose("69", at(0)), at(0), &mut state);
    assert_eq!(opened.transition.kind, TransitionType::ExcursionOpened);
    let next = step(&r, &glucose("72", at(5)), at(5), &mut state);
    assert_eq!(
        (next.root, next.transition.kind),
        (Truth::True, TransitionType::ExcursionContinues)
    );
}

/// With a 15-minute wait, the excursion leaves through the widened limit,
/// waits, and closes; the evaluation after the close reads closed.
#[test]
fn the_evaluation_that_closes_reads_open_and_the_next_reads_closed() {
    let mut r = rule("threshold", low_with_margin());
    r.hysteresis_minutes = 15;
    let mut state = EngineState::new();
    let kinds: Vec<_> = [
        ("69", 0),
        ("74", 5),
        ("76", 10),
        ("74.5", 15),
        ("76", 20),
        ("76", 40),
    ]
    .into_iter()
    .map(|(v, m)| {
        step(&r, &glucose(v, at(m)), at(m), &mut state)
            .transition
            .kind
    })
    .collect();
    assert_eq!(
        kinds,
        vec![
            TransitionType::ExcursionOpened,
            TransitionType::ExcursionContinues,
            TransitionType::HysteresisStarted,
            TransitionType::HysteresisResumed,
            TransitionType::HysteresisStarted,
            TransitionType::ExcursionClosed,
        ]
    );
    let after = step(&r, &glucose("72", at(45)), at(45), &mut state);
    assert_eq!(
        (after.root, after.transition.kind),
        (Truth::False, TransitionType::None)
    );
}

#[test]
fn a_hold_reads_open_only_with_an_open_excursion() {
    let r = rule("threshold", low_with_margin());
    let held = |kind, excursion, last| {
        restored(TrackerState {
            held_since: Some(base() - TimeDelta::minutes(30)),
            updated_at: base() - TimeDelta::minutes(31),
            last_excursion: last,
            ..tracker(kind, excursion)
        })
    };
    let mut with_excursion = held(TrackerStateKind::Active, Some(1), 1);
    let e = step(&r, &glucose("72", base()), base(), &mut with_excursion);
    assert_eq!(
        (e.root, e.transition.kind),
        (Truth::True, TransitionType::ExcursionContinues)
    );
    let mut without = held(TrackerStateKind::Idle, None, 0);
    let e = step(&r, &glucose("72", base()), base(), &mut without);
    assert_eq!(
        (e.root, e.transition.kind),
        (Truth::False, TransitionType::None)
    );
}

/// Open is read from the state kind as restored: a state string the engine
/// does not know is recovered as active while it holds an excursion, and a
/// hold an older engine wrote over changes nothing about it.
#[test]
fn a_stale_or_recovered_state_reads_open_by_its_excursion() {
    let r = rule("threshold", low_with_margin());
    let mut recovered = restored(TrackerState::recovered(
        Some(1),
        1,
        base() - TimeDelta::hours(3),
    ));
    assert_eq!(root_at(&r, "72", &mut recovered), Truth::True);
    let mut stale_hold = restored(TrackerState {
        held_since: Some(base() - TimeDelta::hours(2)),
        updated_at: base() - TimeDelta::hours(1),
        ..tracker(TrackerStateKind::Active, Some(1))
    });
    assert_eq!(root_at(&r, "72", &mut stale_hold), Truth::True);
    let mut no_excursion = restored(tracker(TrackerStateKind::Active, None));
    assert_eq!(root_at(&r, "72", &mut no_excursion), Truth::False);
}

// ---------------------------------------------------------------------------
// Operators, connectives, unknown
// ---------------------------------------------------------------------------

#[test]
fn every_operator_and_direction_widens_toward_its_true_side() {
    let iob = |op: &str| {
        rule(
            "iob",
            json!({ "operator": op, "value": 2, "clear_margin": 1 }),
        )
    };
    let at_iob = |r: &Rule, v: &str, state: &mut EngineState| {
        let ctx = SensorContext {
            iob_units: Some(d(v)),
            ..Default::default()
        };
        step(r, &ctx, base(), state).root
    };
    let cases = [
        (">", "1.5", Truth::True, Truth::False),
        (">", "1", Truth::False, Truth::False),
        (">=", "1", Truth::True, Truth::False),
        (">=", "0.99", Truth::False, Truth::False),
        ("<", "2.5", Truth::True, Truth::False),
        ("<", "3", Truth::False, Truth::False),
        ("<=", "3", Truth::True, Truth::False),
        ("<=", "3.01", Truth::False, Truth::False),
    ];
    for (op, v, opened, closed) in cases {
        let r = iob(op);
        assert_eq!(at_iob(&r, v, &mut open()), opened, "{op} {v} open");
        assert_eq!(
            at_iob(&r, v, &mut EngineState::new()),
            closed,
            "{op} {v} closed"
        );
    }

    let above = rule(
        "threshold",
        json!({ "direction": "above", "value": 180, "clear_margin": 20 }),
    );
    assert_eq!(root_at(&above, "161", &mut open()), Truth::True);
    assert_eq!(root_at(&above, "160", &mut open()), Truth::False);
    assert_eq!(
        root_at(&above, "161", &mut EngineState::new()),
        Truth::False
    );

    let rate = |direction: &str, v: &str, state: &mut EngineState| {
        let r = rule(
            "rate_of_change",
            json!({ "direction": direction, "rate": 2, "clear_margin": 1 }),
        );
        let ctx = SensorContext {
            trend_rate: Some(d(v)),
            ..glucose("100", base())
        };
        step(&r, &ctx, base(), state).root
    };
    assert_eq!(rate("falling", "-1", &mut open()), Truth::True);
    assert_eq!(rate("falling", "-0.9", &mut open()), Truth::False);
    assert_eq!(rate("falling", "-1", &mut EngineState::new()), Truth::False);
    assert_eq!(rate("rising", "1", &mut open()), Truth::True);
    assert_eq!(rate("rising", "0.9", &mut open()), Truth::False);
    assert_eq!(rate("rising", "1", &mut EngineState::new()), Truth::False);
}

#[test]
fn a_margin_of_zero_is_no_margin() {
    let zero = rule(
        "threshold",
        json!({ "direction": "below", "value": 70, "clear_margin": 0 }),
    );
    assert_eq!(root_at(&zero, "70", &mut open()), Truth::False);
    assert_eq!(root_at(&zero, "69.9", &mut open()), Truth::True);
}

#[test]
fn the_margin_applies_under_every_connective_and_not_turns_its_side() {
    let low = node("threshold", low_with_margin());
    let iob = node("iob", json!({ "operator": ">", "value": 1 }));
    let ctx = SensorContext {
        iob_units: Some(d("2")),
        ..glucose("72", base())
    };
    let root = |kind: &str, params: Value, state: &mut EngineState| {
        step(&rule(kind, params), &ctx, base(), state).root
    };

    let and = json!({ "operator": "and", "conditions": [low, iob] });
    assert_eq!(root("composite", and.clone(), &mut open()), Truth::True);
    assert_eq!(
        root("composite", and, &mut EngineState::new()),
        Truth::False
    );

    let or = json!({ "operator": "or", "conditions": [low, node("iob", json!({ "operator": ">", "value": 5 }))] });
    assert_eq!(root("composite", or.clone(), &mut open()), Truth::True);
    assert_eq!(root("composite", or, &mut EngineState::new()), Truth::False);

    let not = rule("not", json!({ "child": low }));
    for (value, opened, closed) in [
        ("72", Truth::True, Truth::True),
        ("66", Truth::True, Truth::False),
        ("65", Truth::True, Truth::False),
        ("64.9", Truth::False, Truth::False),
    ] {
        assert_eq!(root_at(&not, value, &mut open()), opened, "{value} open");
        assert_eq!(
            root_at(&not, value, &mut EngineState::new()),
            closed,
            "{value} closed"
        );
    }

    let double = rule(
        "not",
        json!({ "child": node("not", json!({ "child": low })) }),
    );
    assert_eq!(root_at(&double, "72", &mut open()), Truth::True);
}

/// The leaf log evaluates each leaf alone with the polarity it has in the
/// tree, so it shows the truth the root read.
#[test]
fn the_leaf_log_reads_each_leaf_with_its_polarity() {
    let r = rule(
        "composite",
        json!({ "operator": "or", "conditions": [
            node("threshold", low_with_margin()),
            node("not", json!({ "child": node("threshold", low_with_margin()) })),
        ] }),
    );
    let e = step(&r, &glucose("67", base()), base(), &mut open());
    assert_eq!(e.leaves, Some(vec![Truth::True, Truth::False]));
}

fn issues(
    kind: ConditionKind,
    payload: &Value,
) -> Vec<(String, &'static str, Option<&'static str>)> {
    parse_payload(kind, payload)
        .err()
        .map_or_else(Vec::new, |e| {
            e.0.iter()
                .map(|i| (i.path.clone(), i.reason.code(), i.reason.field()))
                .collect()
        })
}

/// One `sustained` run start cannot serve both the plain and the widened
/// limit, so a margin above zero anywhere under a `sustained` is refused, on
/// the leaf, once, however the `sustained` nodes nest.
#[test]
fn a_margin_under_sustained_is_invalid() {
    let under = |child: Value| json!({ "minutes": 10, "child": child });
    let low = node("threshold", low_with_margin());
    let iob = node("iob", json!({ "operator": ">", "value": 1 }));
    let reason = |path: &str| {
        vec![(
            path.to_owned(),
            "margin_under_sustained",
            Some("clear_margin"),
        )]
    };

    assert_eq!(
        issues(ConditionKind::Sustained, &under(low.clone())),
        reason("sustained[0].threshold")
    );
    assert_eq!(
        issues(
            ConditionKind::Sustained,
            &under(node(
                "composite",
                json!({ "operator": "and", "conditions": [iob, low] })
            ))
        ),
        reason("sustained[0].composite[1].threshold")
    );
    assert_eq!(
        issues(
            ConditionKind::Sustained,
            &under(node("not", json!({ "child": low })))
        ),
        reason("sustained[0].not[0].threshold")
    );
    assert_eq!(
        issues(
            ConditionKind::Sustained,
            &under(node("sustained", under(low.clone())))
        ),
        reason("sustained[0].sustained[0].threshold")
    );
    let zero = node(
        "threshold",
        json!({ "direction": "below", "value": 70, "clear_margin": 0 }),
    );
    assert_eq!(issues(ConditionKind::Sustained, &under(zero)), vec![]);
    assert_eq!(
        issues(
            ConditionKind::Composite,
            &json!({ "operator": "or", "conditions": [low.clone(), node("sustained", under(iob))] })
        ),
        vec![]
    );

    let mut r = rule("iob", json!({ "operator": ">", "value": 1 }));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(node("sustained", under(low)));
    assert!(r.parse_body().is_ok());
    assert!(
        nocturne_alerts_core::engine::parse_auto_resolve(r.auto_resolve_params.as_ref()).is_none()
    );
}

/// A rate margin may bring the open limit to zero, not past it: a "falling"
/// alert then holds while glucose is not rising.
#[test]
fn a_rate_margin_cannot_pass_zero() {
    let rate = |direction: &str, margin: &str| {
        let payload: Value = serde_json::from_str(&format!(
            r#"{{ "direction": "{direction}", "rate": 2, "clear_margin": {margin} }}"#
        ))
        .unwrap();
        issues(ConditionKind::RateOfChange, &payload)
    };
    for direction in ["falling", "rising"] {
        assert_eq!(rate(direction, "2"), vec![], "{direction}");
        assert_eq!(rate(direction, "1.99"), vec![], "{direction}");
        assert_eq!(
            rate(direction, "2.01"),
            vec![(
                "rate_of_change".to_owned(),
                "margin_past_zero",
                Some("clear_margin")
            )],
            "{direction}"
        );
    }
    assert_eq!(
        issues(
            ConditionKind::RateOfChange,
            &json!({ "direction": "rising", "rate": -1, "clear_margin": 0 })
        ),
        vec![]
    );

    let at_zero = rule(
        "rate_of_change",
        json!({ "direction": "falling", "rate": 2, "clear_margin": 2 }),
    );
    let trend = |v: &str, state: &mut EngineState| {
        let ctx = SensorContext {
            trend_rate: Some(d(v)),
            ..glucose("100", base())
        };
        step(&at_zero, &ctx, base(), state).root
    };
    assert_eq!(trend("0", &mut open()), Truth::True);
    assert_eq!(trend("0.01", &mut open()), Truth::False);
}

#[test]
fn the_margin_never_makes_an_unknown_known_or_the_reverse() {
    let r = rule("threshold", low_with_margin());
    let absent = SensorContext {
        last_reading_at: Some(base()),
        ..Default::default()
    };
    assert_eq!(step(&r, &absent, base(), &mut open()).root, Truth::Unknown);
    let stale = glucose("72", base() - TimeDelta::minutes(30));
    assert_eq!(step(&r, &stale, base(), &mut open()).root, Truth::Unknown);
    assert_eq!(
        step(&r, &stale, base(), &mut EngineState::new()).root,
        Truth::Unknown
    );

    let predicted = rule(
        "predicted",
        json!({ "operator": "<", "value": 70, "within_minutes": 30, "clear_margin": 5 }),
    );
    assert_eq!(
        step(&predicted, &SensorContext::default(), base(), &mut open()).root,
        Truth::Unknown
    );
}

#[test]
fn predicted_widens_every_point() {
    let r = rule(
        "predicted",
        json!({ "operator": "<", "value": 70, "within_minutes": 30, "clear_margin": 5 }),
    );
    let ctx = SensorContext {
        predictions: vec![
            PredictedPoint {
                offset_minutes: 10,
                mgdl: d("90"),
            },
            PredictedPoint {
                offset_minutes: 20,
                mgdl: d("73"),
            },
            PredictedPoint {
                offset_minutes: 40,
                mgdl: d("60"),
            },
        ],
        ..glucose("100", base())
    };
    assert_eq!(step(&r, &ctx, base(), &mut open()).root, Truth::True);
    assert_eq!(
        step(&r, &ctx, base(), &mut EngineState::new()).root,
        Truth::False
    );
}

#[test]
fn temp_basal_widens_its_metric() {
    let r = rule(
        "temp_basal",
        json!({ "metric": "rate", "operator": ">", "value": 1, "clear_margin": 0.5 }),
    );
    let ctx = SensorContext {
        active_temp_basal: Some(TempBasalSnapshot {
            rate: d("0.6"),
            scheduled_rate: Some(d("1")),
            started_at: base() - TimeDelta::minutes(5),
        }),
        ..Default::default()
    };
    assert_eq!(step(&r, &ctx, base(), &mut open()).root, Truth::True);
    assert_eq!(
        step(&r, &ctx, base(), &mut EngineState::new()).root,
        Truth::False
    );
}

/// A lower-bound reading decides against the widened limit by the table of
/// §3: `<` is false once the bound is at or past the limit, else unknown.
#[test]
fn a_lower_bound_reservoir_decides_against_the_widened_limit() {
    let r = rule(
        "reservoir",
        json!({ "operator": "<", "value": 20, "clear_margin": 5 }),
    );
    let reservoir = |units: &str| SensorContext {
        reservoir_units: Some(d(units)),
        reservoir_is_lower_bound: true,
        ..Default::default()
    };
    assert_eq!(
        step(&r, &reservoir("22"), base(), &mut EngineState::new()).root,
        Truth::False
    );
    assert_eq!(
        step(&r, &reservoir("22"), base(), &mut open()).root,
        Truth::Unknown
    );
    assert_eq!(
        step(&r, &reservoir("25"), base(), &mut open()).root,
        Truth::False
    );

    let gt = rule(
        "reservoir",
        json!({ "operator": ">", "value": 50, "clear_margin": 10 }),
    );
    assert_eq!(
        step(&gt, &reservoir("41"), base(), &mut open()).root,
        Truth::True
    );
    assert_eq!(
        step(&gt, &reservoir("41"), base(), &mut EngineState::new()).root,
        Truth::Unknown
    );
}

// ---------------------------------------------------------------------------
// Auto-resolve, next change
// ---------------------------------------------------------------------------

/// A resolve tree is read only while the excursion is open, so a margin there
/// would always apply and be another limit. It is read against its plain
/// limit.
#[test]
fn an_auto_resolve_tree_reads_its_plain_limit() {
    let mut r = rule("iob", json!({ "operator": ">", "value": 1 }));
    r.auto_resolve_enabled = true;
    r.auto_resolve_params = Some(node(
        "threshold",
        json!({ "direction": "above", "value": 80, "clear_margin": 10 }),
    ));
    let ctx = |value: &str, now| SensorContext {
        iob_units: Some(d("2")),
        ..glucose(value, now)
    };
    let mut state = EngineState::new();
    let opened = step(&r, &ctx("65", at(0)), at(0), &mut state);
    assert_eq!(opened.transition.kind, TransitionType::ExcursionOpened);
    let e = step(&r, &ctx("75", at(5)), at(5), &mut state);
    assert!(!e.auto_resolved);
    let e = step(&r, &ctx("81", at(10)), at(10), &mut state);
    assert!(e.auto_resolved);
}

/// An auto-resolve closes an excursion whose body held against its widened
/// limit; the next evaluation reads the plain limit and can answer otherwise,
/// so it is due at once. Without the margin nothing is due before the reading
/// goes stale.
#[test]
fn a_flag_change_on_a_margined_body_is_due_at_once() {
    let resolving = |params: Value| {
        let mut r = rule("threshold", params);
        r.auto_resolve_enabled = true;
        r.auto_resolve_params = Some(node("iob", json!({ "operator": ">", "value": 1 })));
        r
    };
    let ctx = SensorContext {
        iob_units: Some(d("2")),
        ..glucose("72", base())
    };
    let tick = base() + TimeDelta::nanoseconds(100);
    let stale = base() + TimeDelta::minutes(11);

    let margined = resolving(low_with_margin());
    let e = step(&margined, &ctx, base(), &mut open());
    assert!(e.auto_resolved);
    assert_eq!(e.next_change_at, Some(tick));

    let plain = resolving(json!({ "direction": "below", "value": 75 }));
    let e = step(&plain, &ctx, base(), &mut open());
    assert!(e.auto_resolved);
    assert_eq!(e.next_change_at, Some(stale));

    let zero = resolving(json!({ "direction": "below", "value": 75, "clear_margin": 0 }));
    let e = step(&zero, &ctx, base(), &mut open());
    assert_eq!(e.next_change_at, Some(stale));
}

/// `not(below 70, margin 5)` sitting at 72 opens once and stays open: under
/// `not` the margin widens the rule's truth too, so the evaluation after the
/// opening, read open, continues rather than leaving and opening again.
#[test]
fn an_opening_under_not_continues_on_the_next() {
    let r = rule(
        "not",
        json!({ "child": node("threshold", low_with_margin()) }),
    );
    let mut state = EngineState::new();
    let evaluations: Vec<_> = (0..6)
        .map(|i| step(&r, &glucose("72", at(5 * i)), at(5 * i), &mut state))
        .collect();
    let kinds: Vec<_> = evaluations.iter().map(|e| e.transition.kind).collect();
    assert_eq!(kinds[0], TransitionType::ExcursionOpened);
    assert_eq!(
        evaluations[0].next_change_at,
        Some(at(0) + TimeDelta::minutes(11)),
        "an opening reads at least as true next, so nothing is due at once"
    );
    assert!(
        kinds[1..]
            .iter()
            .all(|k| *k == TransitionType::ExcursionContinues)
    );
}

// ---------------------------------------------------------------------------
// Properties, against an oracle from the definition
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Leaf {
    Below,
    Above,
    Falling,
    Rising,
    Iob(&'static str),
    Predicted(&'static str),
}

#[derive(Debug, Clone)]
struct Case {
    leaf: Leaf,
    value: i64,
    margin: i64,
    hysteresis: i32,
    readings: Vec<i64>,
}

impl Case {
    fn params(&self, margin: Option<i64>) -> (&'static str, Value) {
        let mut params = match self.leaf {
            Leaf::Below => json!({ "direction": "below", "value": self.value }),
            Leaf::Above => json!({ "direction": "above", "value": self.value }),
            Leaf::Falling => json!({ "direction": "falling", "rate": self.value }),
            Leaf::Rising => json!({ "direction": "rising", "rate": self.value }),
            Leaf::Iob(op) => json!({ "operator": op, "value": self.value }),
            Leaf::Predicted(op) => {
                json!({ "operator": op, "value": self.value, "within_minutes": 30 })
            }
        };
        if let Some(m) = margin {
            params["clear_margin"] = json!(m);
        }
        (self.kind(), params)
    }

    fn kind(&self) -> &'static str {
        match self.leaf {
            Leaf::Below | Leaf::Above => "threshold",
            Leaf::Falling | Leaf::Rising => "rate_of_change",
            Leaf::Iob(_) => "iob",
            Leaf::Predicted(_) => "predicted",
        }
    }

    fn rule(&self, margin: Option<i64>) -> Rule {
        let (kind, params) = self.params(margin);
        let mut r = rule(kind, params);
        r.hysteresis_minutes = self.hysteresis;
        r.on_unknown = OnUnknown::Hold;
        r
    }

    fn context(&self, reading: i64, now: DateTime<Utc>) -> SensorContext {
        let v = Decimal::from(reading);
        let fresh = glucose("100", now);
        match self.leaf {
            Leaf::Below | Leaf::Above => glucose(&reading.to_string(), now),
            Leaf::Falling | Leaf::Rising => SensorContext {
                trend_rate: Some(v),
                ..fresh
            },
            Leaf::Iob(_) => SensorContext {
                iob_units: Some(v),
                ..fresh
            },
            Leaf::Predicted(_) => SensorContext {
                predictions: vec![PredictedPoint {
                    offset_minutes: 5,
                    mgdl: v,
                }],
                ..fresh
            },
        }
    }

    /// The leaf's truth from the definition: open, the limit moves by the
    /// margin toward the side the comparison holds on; closed, it does not.
    fn oracle(&self, reading: i64, open: bool) -> bool {
        let (v, m) = (self.value, if open { self.margin } else { 0 });
        let compare = |op: &str| match op {
            ">" => reading > v - m,
            ">=" => reading >= v - m,
            "<" => reading < v + m,
            "<=" => reading <= v + m,
            _ => unreachable!(),
        };
        match self.leaf {
            Leaf::Below => reading < v + m,
            Leaf::Above => reading > v - m,
            Leaf::Falling => reading <= -v + m,
            Leaf::Rising => reading >= v - m,
            Leaf::Iob(op) | Leaf::Predicted(op) => compare(op),
        }
    }
}

fn case() -> impl Strategy<Value = Case> {
    let op = || proptest::sample::select(vec![">", ">=", "<", "<="]);
    let leaf = prop_oneof![
        Just(Leaf::Below),
        Just(Leaf::Above),
        Just(Leaf::Falling),
        Just(Leaf::Rising),
        op().prop_map(Leaf::Iob),
        op().prop_map(Leaf::Predicted),
    ];
    (leaf, 0..=15i32).prop_flat_map(|(leaf, hysteresis)| {
        let (values, readings) = match leaf {
            Leaf::Below | Leaf::Above | Leaf::Predicted(_) => (60..=80i64, 50..=95i64),
            Leaf::Falling | Leaf::Rising => (1..=3, -6..=6),
            Leaf::Iob(_) => (0..=4, -2..=8),
        };
        (
            Just(leaf),
            values,
            Just(hysteresis),
            proptest::collection::vec(readings, 1..=24),
        )
            .prop_flat_map(|(leaf, value, hysteresis, readings)| {
                let largest = match leaf {
                    Leaf::Falling | Leaf::Rising => value,
                    Leaf::Iob(_) => 3,
                    _ => 10,
                };
                (0..=largest).prop_map(move |margin| Case {
                    leaf,
                    value,
                    margin,
                    hysteresis,
                    readings: readings.clone(),
                })
            })
    })
}

fn sample(count: usize) -> Vec<Case> {
    let mut runner = TestRunner::deterministic();
    let strategy = case();
    (0..count)
        .map(|_| strategy.new_tree(&mut runner).unwrap().current())
        .collect()
}

/// Every tick five minutes after the one before, with whether the excursion
/// was open before the evaluation and its outcome. Open is read off the
/// transitions so far, not off the engine: an opening opens it, a close
/// closes it, and every other transition (a wait started or resumed, a
/// continue, none) leaves it as it was.
fn run(c: &Case, r: &Rule) -> Vec<(i64, bool, Evaluation)> {
    let mut state = EngineState::new();
    let mut open = false;
    c.readings
        .iter()
        .enumerate()
        .map(|(i, &reading)| {
            let now = at(5 * i64::try_from(i).unwrap());
            let before = open;
            let e = step(r, &c.context(reading, now), now, &mut state);
            open = match e.transition.kind {
                TransitionType::ExcursionOpened => true,
                TransitionType::ExcursionClosed => false,
                _ => open,
            };
            (reading, before, e)
        })
        .collect()
}

#[test]
fn no_margin_and_a_zero_margin_give_the_same_outcome() {
    for c in sample(400) {
        let absent = run(&c, &c.rule(None));
        let zero = run(&c, &c.rule(Some(0)));
        for ((_, _, a), (_, _, z)) in absent.iter().zip(&zero) {
            let outcome = |e: &Evaluation| {
                nocturne_alerts_core::engine::RuleOutcome {
                    rule_id: RULE_ID,
                    evaluation: Some(e.clone()),
                }
                .to_json()
            };
            assert_eq!(outcome(a), outcome(z), "{c:?}");
        }
    }
}

#[test]
fn closed_a_margined_rule_opens_exactly_when_the_plain_one_does() {
    let mut opened = 0;
    for c in sample(400) {
        let plain = run(&c, &c.rule(None));
        let margined = run(&c, &c.rule(Some(c.margin)));
        for ((_, _, p), (_, _, m)) in plain.iter().zip(&margined) {
            assert_eq!(p.transition.kind, m.transition.kind, "{c:?}");
            if p.transition.kind == TransitionType::ExcursionOpened {
                opened += 1;
                break;
            }
        }
    }
    eprintln!("closed rules that opened: {opened}");
    assert!(opened > 50, "vacuous: only {opened} openings");
}

#[test]
fn open_the_leaf_is_true_exactly_when_the_widened_comparison_holds() {
    let (mut open_cases, mut in_band) = (0, 0);
    for c in sample(400) {
        let r = c.rule(Some(c.margin));
        for (reading, open, e) in run(&c, &r) {
            assert_eq!(
                e.root,
                Truth::from(c.oracle(reading, open)),
                "{c:?} {reading} {open}"
            );
            if open {
                open_cases += 1;
                if c.oracle(reading, true) && !c.oracle(reading, false) {
                    in_band += 1;
                }
            }
        }
    }
    eprintln!("open evaluations: {open_cases}, inside the margin only: {in_band}");
    assert!(
        in_band > 50,
        "vacuous: only {in_band} evaluations inside the margin"
    );
}

#[test]
fn an_excursion_never_leaves_while_the_value_is_inside_the_widened_limit() {
    let mut inside = 0;
    for c in sample(400) {
        let r = c.rule(Some(c.margin));
        for (reading, open, e) in run(&c, &r) {
            if open && c.oracle(reading, true) {
                inside += 1;
                assert!(
                    matches!(
                        e.transition.kind,
                        TransitionType::ExcursionContinues | TransitionType::HysteresisResumed
                    ),
                    "{c:?} {reading}: {:?}",
                    e.transition.kind
                );
            }
        }
    }
    eprintln!("open evaluations inside the widened limit: {inside}");
    assert!(inside > 50, "vacuous: only {inside}");
}

/// A tree of margined leaves under `not`, `and` and `or`.
#[derive(Debug, Clone)]
enum Tree {
    Below {
        value: i64,
        margin: i64,
    },
    Above {
        value: i64,
        margin: i64,
    },
    Iob {
        op: &'static str,
        value: i64,
        margin: i64,
    },
    Reservoir {
        op: &'static str,
        value: i64,
        margin: i64,
    },
    Rate {
        falling: bool,
        rate: i64,
        margin: i64,
    },
    /// A `cob` leaf whose fact the context never carries: unknown.
    Absent {
        margin: i64,
    },
    Not(Box<Tree>),
    All(Vec<Tree>),
    Any(Vec<Tree>),
}

/// The facts a tree is read against.
#[derive(Debug, Clone, Copy)]
struct Facts {
    glucose: i64,
    iob: i64,
    trend: i64,
    reservoir: i64,
    reservoir_lower_bound: bool,
}

impl Facts {
    fn context(self) -> SensorContext {
        SensorContext {
            iob_units: Some(Decimal::from(self.iob)),
            trend_rate: Some(Decimal::from(self.trend)),
            reservoir_units: Some(Decimal::from(self.reservoir)),
            reservoir_is_lower_bound: self.reservoir_lower_bound,
            ..glucose(&self.glucose.to_string(), base())
        }
    }
}

/// Three-valued truth: `None` is unknown.
type Kleene = Option<bool>;

fn rank(t: Kleene) -> u8 {
    match t {
        Some(false) => 0,
        None => 1,
        Some(true) => 2,
    }
}

fn as_truth(t: Kleene) -> Truth {
    t.map_or(Truth::Unknown, Truth::from)
}

fn compare(op: &str, x: i64, limit: i64) -> bool {
    match op {
        ">" => x > limit,
        ">=" => x >= limit,
        "<" => x < limit,
        "<=" => x <= limit,
        _ => unreachable!(),
    }
}

impl Tree {
    fn json(&self) -> Value {
        match self {
            Tree::Below { value, margin } => node(
                "threshold",
                json!({ "direction": "below", "value": value, "clear_margin": margin }),
            ),
            Tree::Above { value, margin } => node(
                "threshold",
                json!({ "direction": "above", "value": value, "clear_margin": margin }),
            ),
            Tree::Iob { op, value, margin } => node(
                "iob",
                json!({ "operator": op, "value": value, "clear_margin": margin }),
            ),
            Tree::Reservoir { op, value, margin } => node(
                "reservoir",
                json!({ "operator": op, "value": value, "clear_margin": margin }),
            ),
            Tree::Rate {
                falling,
                rate,
                margin,
            } => node(
                "rate_of_change",
                json!({
                    "direction": if *falling { "falling" } else { "rising" },
                    "rate": rate,
                    "clear_margin": margin,
                }),
            ),
            Tree::Absent { margin } => node(
                "cob",
                json!({ "operator": ">", "value": 1, "clear_margin": margin }),
            ),
            Tree::Not(child) => node("not", json!({ "child": child.json() })),
            Tree::All(children) | Tree::Any(children) => node(
                "composite",
                json!({
                    "operator": if matches!(self, Tree::All(_)) { "and" } else { "or" },
                    "conditions": children.iter().map(Tree::json).collect::<Vec<_>>(),
                }),
            ),
        }
    }

    /// The truth from the definition: open, each leaf's limit moves by its
    /// margin toward the side that keeps the whole rule true, which is the
    /// leaf's own true side under an even number of `not`s and its false side
    /// under an odd number. A lower-bound reservoir reading `b` means "at
    /// least `b`", so it decides only where every amount from `b` up agrees.
    /// Connectives are Kleene's.
    fn oracle(&self, f: Facts, open: bool, negated: bool) -> Kleene {
        let shift = |margin: i64| match (open, negated) {
            (false, _) => 0,
            (true, false) => margin,
            (true, true) => -margin,
        };
        let toward_true = |op: &str, value: i64, margin: i64| match op {
            ">" | ">=" => value - shift(margin),
            _ => value + shift(margin),
        };
        match self {
            Tree::Below { value, margin } => Some(f.glucose < value + shift(*margin)),
            Tree::Above { value, margin } => Some(f.glucose > value - shift(*margin)),
            Tree::Iob { op, value, margin } => {
                Some(compare(op, f.iob, toward_true(op, *value, *margin)))
            }
            Tree::Reservoir { op, value, margin } => {
                let limit = toward_true(op, *value, *margin);
                let b = f.reservoir;
                if !f.reservoir_lower_bound {
                    return Some(compare(op, b, limit));
                }
                let from_b_up: Vec<bool> = (b..=b + 200).map(|x| compare(op, x, limit)).collect();
                if from_b_up.iter().all(|&t| t) {
                    Some(true)
                } else if from_b_up.iter().all(|&t| !t) {
                    Some(false)
                } else {
                    None
                }
            }
            Tree::Rate {
                falling,
                rate,
                margin,
            } => Some(if *falling {
                f.trend <= -rate + shift(*margin)
            } else {
                f.trend >= rate - shift(*margin)
            }),
            Tree::Absent { .. } => None,
            Tree::Not(child) => child.oracle(f, open, !negated).map(|t| !t),
            Tree::All(children) => {
                let all: Vec<_> = children
                    .iter()
                    .map(|c| c.oracle(f, open, negated))
                    .collect();
                if all.contains(&Some(false)) {
                    Some(false)
                } else if all.contains(&None) {
                    None
                } else {
                    Some(true)
                }
            }
            Tree::Any(children) => {
                let any: Vec<_> = children
                    .iter()
                    .map(|c| c.oracle(f, open, negated))
                    .collect();
                if any.contains(&Some(true)) {
                    Some(true)
                } else if any.contains(&None) {
                    None
                } else {
                    Some(false)
                }
            }
        }
    }

    fn has_not(&self) -> bool {
        match self {
            Tree::Not(_) => true,
            Tree::All(children) | Tree::Any(children) => children.iter().any(Tree::has_not),
            _ => false,
        }
    }

    fn has_lower_bound_reservoir(&self) -> bool {
        match self {
            Tree::Reservoir { .. } => true,
            Tree::Not(child) => child.has_lower_bound_reservoir(),
            Tree::All(children) | Tree::Any(children) => {
                children.iter().any(Tree::has_lower_bound_reservoir)
            }
            _ => false,
        }
    }

    fn has_margin(&self) -> bool {
        match self {
            Tree::Below { margin, .. }
            | Tree::Above { margin, .. }
            | Tree::Iob { margin, .. }
            | Tree::Reservoir { margin, .. }
            | Tree::Rate { margin, .. }
            | Tree::Absent { margin } => *margin > 0,
            Tree::Not(child) => child.has_margin(),
            Tree::All(children) | Tree::Any(children) => children.iter().any(Tree::has_margin),
        }
    }
}

fn tree() -> impl Strategy<Value = Tree> {
    let op = || proptest::sample::select(vec![">", ">=", "<", "<="]);
    let leaf = prop_oneof![
        (60..=80i64, 0..=10i64).prop_map(|(value, margin)| Tree::Below { value, margin }),
        (60..=80i64, 0..=10i64).prop_map(|(value, margin)| Tree::Above { value, margin }),
        (op(), 0..=4i64, 0..=3i64).prop_map(|(op, value, margin)| Tree::Iob { op, value, margin }),
        (op(), 10..=30i64, 0..=5i64).prop_map(|(op, value, margin)| Tree::Reservoir {
            op,
            value,
            margin
        }),
        (any::<bool>(), 1..=3i64)
            .prop_flat_map(|(falling, rate)| (Just(falling), Just(rate), 0..=rate))
            .prop_map(|(falling, rate, margin)| Tree::Rate {
                falling,
                rate,
                margin
            }),
        (0..=3i64).prop_map(|margin| Tree::Absent { margin }),
    ];
    leaf.prop_recursive(3, 12, 3, |inner| {
        prop_oneof![
            2 => inner.clone().prop_map(|c| Tree::Not(Box::new(c))),
            1 => proptest::collection::vec(inner.clone(), 1..=3).prop_map(Tree::All),
            1 => proptest::collection::vec(inner, 1..=3).prop_map(Tree::Any),
        ]
    })
}

fn facts() -> impl Strategy<Value = Facts> {
    (50..=95i64, -2..=8i64, -6..=6i64, 0..=40i64, any::<bool>()).prop_map(
        |(glucose, iob, trend, reservoir, reservoir_lower_bound)| Facts {
            glucose,
            iob,
            trend,
            reservoir,
            reservoir_lower_bound,
        },
    )
}

fn tree_rule(t: &Tree) -> Rule {
    let params = t.json();
    let kind = params["type"].as_str().unwrap().to_owned();
    rule(&kind, params[kind.as_str()].clone())
}

/// Every margined rule is monotone in the open flag, in Kleene's order
/// (false, unknown, true): a root is at least as true open as closed, so an
/// opening never reads less true on the evaluation after it and a closing
/// never reads more true on the one after it.
#[test]
fn every_margined_rule_is_monotone_in_the_open_flag() {
    let mut runner = TestRunner::deterministic();
    let strategy = (tree(), facts());
    let (mut widened, mut widened_under_not, mut unknown_closed, mut lower_bound_moved) =
        (0, 0, 0, 0);
    for _ in 0..3000 {
        let (t, f) = strategy.new_tree(&mut runner).unwrap().current();
        let r = tree_rule(&t);
        let closed = step(&r, &f.context(), base(), &mut EngineState::new()).root;
        let opened = step(&r, &f.context(), base(), &mut open()).root;
        let expect_closed = t.oracle(f, false, false);
        let expect_open = t.oracle(f, true, false);
        assert_eq!(closed, as_truth(expect_closed), "{t:?} {f:?} closed");
        assert_eq!(opened, as_truth(expect_open), "{t:?} {f:?} open");
        assert!(
            rank(expect_open) >= rank(expect_closed),
            "{t:?} {f:?} not monotone"
        );
        if rank(expect_open) > rank(expect_closed) {
            widened += 1;
            if t.has_not() {
                widened_under_not += 1;
            }
            if f.reservoir_lower_bound && t.has_lower_bound_reservoir() {
                lower_bound_moved += 1;
            }
        }
        if expect_closed.is_none() {
            unknown_closed += 1;
        }
    }
    eprintln!(
        "roots the margin moved up: {widened}, with a not: {widened_under_not}, \
         with a lower-bound reservoir: {lower_bound_moved}; unknown closed: {unknown_closed}"
    );
    assert!(
        widened_under_not > 50,
        "vacuous: only {widened_under_not} under not"
    );
    assert!(
        lower_bound_moved > 10,
        "vacuous: only {lower_bound_moved} lower-bound"
    );
    assert!(
        unknown_closed > 50,
        "vacuous: only {unknown_closed} unknown"
    );
}

/// No tree with a margin above zero under a `sustained`, however deep,
/// reaches evaluation; one with none does.
#[test]
fn a_tree_with_a_margin_under_sustained_never_parses() {
    let mut runner = TestRunner::deterministic();
    let (mut refused, mut parsed) = (0, 0);
    for _ in 0..1000 {
        let (t, minutes) = (tree(), 1..=30i64).new_tree(&mut runner).unwrap().current();
        let wrapped = json!({ "minutes": minutes, "child": t.json() });
        let result = parse_payload(ConditionKind::Sustained, &wrapped);
        if t.has_margin() {
            let reasons: Vec<_> = result.unwrap_err().0.iter().map(|i| i.reason).collect();
            assert!(!reasons.is_empty(), "{t:?}");
            assert!(
                reasons.iter().all(|r| *r == Reason::MarginUnderSustained),
                "{t:?}: {reasons:?}"
            );
            refused += 1;
        } else {
            assert!(result.is_ok(), "{t:?}");
            parsed += 1;
        }
    }
    eprintln!("refused: {refused}, parsed: {parsed}");
    assert!(
        refused > 100 && parsed > 10,
        "vacuous: {refused} refused, {parsed} parsed"
    );
}
