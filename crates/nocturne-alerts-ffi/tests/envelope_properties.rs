//! Property tests of the C ABI envelopes: every entry point answers any input
//! with a JSON envelope, `validate` agrees with the core readers, and
//! `evaluate` threaded through its own response matches the core run
//! (README "Envelopes").

#![allow(
    unsafe_code,
    clippy::undocumented_unsafe_blocks,
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    clippy::arithmetic_side_effects,
    reason = "test code calling the C ABI"
)]

#[path = "../../nocturne-alerts-core/tests/common/generators.rs"]
mod generators;

use std::ffi::{CStr, CString, c_char};

use chrono::{DateTime, SecondsFormat, TimeDelta, Utc};
use proptest::collection::vec;
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use proptest::strategy::ValueTree;
use proptest::test_runner::TestRunner;
use serde_json::{Map, Value, json};

use nocturne_alerts::{
    nocturne_alerts_classify, nocturne_alerts_describe, nocturne_alerts_evaluate,
    nocturne_alerts_evaluate_node, nocturne_alerts_free_string, nocturne_alerts_leaf_paths,
    nocturne_alerts_references_any, nocturne_alerts_references_wall_clock, nocturne_alerts_replay,
    nocturne_alerts_rule_reads, nocturne_alerts_tracker_close_elapsed_hysteresis,
    nocturne_alerts_tracker_force_close, nocturne_alerts_tracker_process, nocturne_alerts_validate,
};
use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::engine::{EngineState, Rule, WireRule, evaluate_rule};
use nocturne_alerts_core::enums::WireEnum;
use nocturne_alerts_core::facts::ContextFact;
use nocturne_alerts_core::model::{ConditionKind, Node, ParseError, parse_payload};

use generators::{
    RULE_ID, arbitrary_json, base, mutated, mutations, payload, rule_spec, ticks, tree, word,
};

type Entry = unsafe extern "C" fn(*const c_char) -> *mut c_char;

fn call(f: Entry, input: &str) -> String {
    let input = CString::new(input).expect("the input holds no NUL");
    unsafe {
        let ptr = f(input.as_ptr());
        assert!(!ptr.is_null(), "an entry point returned null");
        let out = CStr::from_ptr(ptr).to_str().expect("UTF-8").to_owned();
        nocturne_alerts_free_string(ptr);
        out
    }
}

fn call_json(f: Entry, input: &str) -> Value {
    serde_json::from_str(&call(f, input)).expect("an entry point returned JSON")
}

/// The fields one request key carries, with the swap to an arbitrary value
/// that lets the envelope's own checks see the wrong shape now and then.
type Group = BoxedStrategy<Vec<(&'static str, Value)>>;

fn swappable(key: &'static str, value: BoxedStrategy<Value>) -> Group {
    prop_oneof![30 => value, 1 => arbitrary_json()]
        .prop_map(move |v| vec![(key, v)])
        .boxed()
}

fn request(required: Vec<Group>, optional: Vec<Group>) -> BoxedStrategy<Value> {
    let version = swappable("schema_version", Just(json!(1)).boxed());
    let required: Vec<Group> = required.into_iter().chain([version]).collect();
    let optional: Vec<_> = optional
        .into_iter()
        .map(|group| option::weighted(0.5, group).prop_map(Option::unwrap_or_default))
        .collect();
    (required, optional)
        .prop_map(|(required, optional)| {
            required
                .into_iter()
                .chain(optional)
                .flatten()
                .map(|(key, value)| (key.to_owned(), value))
                .collect::<Map<_, _>>()
                .into()
        })
        .boxed()
}

fn instant(at: DateTime<Utc>) -> Value {
    Value::from(at.to_rfc3339_opts(SecondsFormat::Secs, true))
}

fn timestamp() -> BoxedStrategy<Value> {
    prop_oneof![
        30 => (-86_400..86_400i64).prop_map(|s| instant(base() + TimeDelta::seconds(s))),
        1 => (-1_000_000_000_000i64..300_000_000_000i64)
            .prop_map(|s| instant(DateTime::from_timestamp(s, 0).unwrap_or_default())),
    ]
    .boxed()
}

fn tracker_object() -> BoxedStrategy<Value> {
    let state = select(vec!["idle", "confirming", "active", "hysteresis", "stale"]);
    (
        state,
        0..5i32,
        option::of(0..5u32),
        timestamp(),
        option::of(timestamp()),
        any::<bool>(),
        0..5u32,
    )
        .prop_map(|(state, count, excursion, updated, started, rearm, next)| {
            json!({
                "state": state,
                "confirmation_count": count,
                "active_excursion_ordinal": excursion,
                "updated_at": updated,
                "hysteresis_started_at": started,
                "awaiting_rearm": rearm,
                "next_excursion_ordinal": next,
            })
        })
        .boxed()
}

fn condition_pair() -> Group {
    let valid = select(ConditionKind::ALL.to_vec()).prop_flat_map(|kind| {
        payload(kind, &tree()).prop_map(move |params| (Value::from(kind.name()), params))
    });
    prop_oneof![
        30 => valid,
        1 => ("[a-z_]{0,8}", arbitrary_json()).prop_map(|(t, p)| (Value::from(t), p)),
    ]
    .prop_map(|(kind, params)| vec![("condition_type", kind), ("condition_params", params)])
    .boxed()
}

fn rule_wire() -> BoxedStrategy<Value> {
    rule_spec().prop_map(|r| r.wire()).boxed()
}

fn first_context() -> BoxedStrategy<Value> {
    ticks().prop_map(|t| t[0].context.clone()).boxed()
}

fn timers() -> BoxedStrategy<Value> {
    vec(("composite\\[[0-3]\\]\\.sustained", timestamp()), 0..3)
        .prop_map(|pairs| Value::Object(pairs.into_iter().collect()))
        .boxed()
}

fn flag() -> BoxedStrategy<Value> {
    any::<bool>().prop_map(Value::from).boxed()
}

fn tracker_config() -> BoxedStrategy<Value> {
    (option::of(-2..5i32), option::of(-2..40i32))
        .prop_map(|(c, h)| {
            let mut config = Map::new();
            if let Some(c) = c {
                config.insert("confirmation_readings".into(), c.into());
            }
            if let Some(h) = h {
                config.insert("hysteresis_minutes".into(), h.into());
            }
            Value::Object(config)
        })
        .boxed()
}

fn tracker_request(required: Vec<Group>) -> BoxedStrategy<Value> {
    request(
        [required, vec![swappable("now", timestamp())]].concat(),
        vec![swappable("tracker", tracker_object())],
    )
}

fn leaf_paths_input() -> BoxedStrategy<Value> {
    prop_oneof![
        3 => tree(),
        1 => (tree(), select(vec!["auto_resolve", "snooze", "x"]))
            .prop_map(|(node, root)| json!({ "node": node, "root": root })),
    ]
    .boxed()
}

fn rule_body_request(extra_required: Vec<Group>, optional: Vec<Group>) -> BoxedStrategy<Value> {
    request([vec![condition_pair()], extra_required].concat(), optional)
}

struct EntryPoint {
    name: &'static str,
    call: Entry,
    request: fn() -> BoxedStrategy<Value>,
}

fn entry_points() -> Vec<EntryPoint> {
    vec![
        EntryPoint {
            name: "evaluate",
            call: nocturne_alerts_evaluate,
            request: || {
                request(
                    vec![
                        swappable("rule", rule_wire()),
                        swappable("context", first_context()),
                        swappable("now", timestamp()),
                    ],
                    vec![
                        swappable("timers", timers()),
                        swappable("tracker", tracker_object()),
                        swappable("include_leaves", flag()),
                    ],
                )
            },
        },
        EntryPoint {
            name: "evaluate_node",
            call: nocturne_alerts_evaluate_node,
            request: || {
                request(
                    vec![
                        swappable("rule_id", Just(Value::from(RULE_ID.to_string())).boxed()),
                        swappable("node", tree()),
                        swappable("context", first_context()),
                        swappable("now", timestamp()),
                    ],
                    vec![
                        swappable("timers", timers()),
                        swappable(
                            "root",
                            select(vec!["auto_resolve", "snooze", "x"])
                                .prop_map(Value::from)
                                .boxed(),
                        ),
                    ],
                )
            },
        },
        EntryPoint {
            name: "validate",
            call: nocturne_alerts_validate,
            request: || {
                rule_body_request(
                    vec![],
                    vec![
                        swappable("auto_resolve_params", tree()),
                        swappable(
                            "snooze_conditions",
                            vec(tree(), 0..3).prop_map(Value::from).boxed(),
                        ),
                    ],
                )
            },
        },
        EntryPoint {
            name: "classify",
            call: nocturne_alerts_classify,
            request: || rule_body_request(vec![], vec![]),
        },
        EntryPoint {
            name: "leaf_paths",
            call: nocturne_alerts_leaf_paths,
            request: leaf_paths_input,
        },
        EntryPoint {
            name: "describe",
            call: nocturne_alerts_describe,
            request: || rule_body_request(vec![], vec![swappable("auto_resolve_params", tree())]),
        },
        EntryPoint {
            name: "rule_reads",
            call: nocturne_alerts_rule_reads,
            request: || {
                rule_body_request(
                    vec![],
                    vec![
                        swappable("auto_resolve_enabled", flag()),
                        swappable("auto_resolve_params", tree()),
                    ],
                )
            },
        },
        EntryPoint {
            name: "references_any",
            call: nocturne_alerts_references_any,
            request: || {
                rule_body_request(
                    vec![swappable(
                        "facts",
                        vec(word::<ContextFact>(), 0..3)
                            .prop_map(Value::from)
                            .boxed(),
                    )],
                    vec![
                        swappable("auto_resolve_enabled", flag()),
                        swappable("auto_resolve_params", tree()),
                    ],
                )
            },
        },
        EntryPoint {
            name: "references_wall_clock",
            call: nocturne_alerts_references_wall_clock,
            request: || {
                rule_body_request(
                    vec![],
                    vec![
                        swappable("auto_resolve_enabled", flag()),
                        swappable("auto_resolve_params", tree()),
                    ],
                )
            },
        },
        EntryPoint {
            name: "replay",
            call: nocturne_alerts_replay,
            request: || {
                let ticks = ticks().prop_map(|ticks| {
                    Value::from(
                        ticks
                            .into_iter()
                            .map(|t| json!({ "at": instant(t.now), "context": t.context }))
                            .collect::<Vec<_>>(),
                    )
                });
                request(
                    vec![
                        swappable("rules", rule_wire().prop_map(|r| json!([r])).boxed()),
                        swappable("ticks", ticks.boxed()),
                    ],
                    vec![swappable("include_ticks", flag())],
                )
            },
        },
        EntryPoint {
            name: "tracker_process",
            call: nocturne_alerts_tracker_process,
            request: || {
                tracker_request(vec![
                    swappable("config", tracker_config()),
                    swappable("condition_met", flag()),
                ])
            },
        },
        EntryPoint {
            name: "tracker_force_close",
            call: nocturne_alerts_tracker_force_close,
            request: || {
                tracker_request(vec![swappable(
                    "reason",
                    select(vec!["hysteresis", "auto", "manual", "rule-disabled"])
                        .prop_map(Value::from)
                        .boxed(),
                )])
            },
        },
        EntryPoint {
            name: "tracker_close_elapsed_hysteresis",
            call: nocturne_alerts_tracker_close_elapsed_hysteresis,
            request: || tracker_request(vec![swappable("config", tracker_config())]),
        },
    ]
}

fn arbitrary_object() -> BoxedStrategy<Value> {
    arbitrary_json()
        .prop_map(|v| match v {
            Value::Object(_) => v,
            other => json!({ "schema_version": 1, "value": other }),
        })
        .boxed()
}

fn input_text(entry: &EntryPoint) -> BoxedStrategy<String> {
    prop_oneof![
        8 => (entry.request)().prop_map(|v| v.to_string()),
        1 => arbitrary_object().prop_map(|v| v.to_string()),
        1 => "[^\\x00]{0,200}",
    ]
    .boxed()
}

fn check_envelope(name: &str, out: &Value) -> Result<(), TestCaseError> {
    let ok = out.get("ok").and_then(Value::as_bool);
    prop_assert!(ok.is_some(), "{name}: no boolean ok in {out}");
    if ok == Some(false) {
        let error = out.get("error").and_then(Value::as_str);
        prop_assert!(error.is_some(), "{name}: an error without a message");
        prop_assert!(
            !error
                .unwrap_or_default()
                .starts_with("panic in alert engine"),
            "{name}: {out}"
        );
    }
    Ok(())
}

/// Every entry point answers any text, any JSON object and its own
/// request-shaped objects with a JSON envelope that has a boolean `ok`, and
/// never through the panic guard.
#[test]
fn every_entry_point_answers_any_input_with_an_envelope() {
    for entry in entry_points() {
        TestRunner::default()
            .run(&input_text(&entry), |input| {
                check_envelope(entry.name, &call_json(entry.call, &input))
            })
            .unwrap_or_else(|e| panic!("{}: {e}", entry.name));
    }
}

/// Each entry point's own request generator reaches `ok: true`, so the
/// previous property exercises the working path and not only rejection.
/// Rates measured over 256 requests: evaluate 199, evaluate_node 203,
/// validate 250, classify 246, leaf_paths 256, describe 238, rule_reads 242,
/// references_any 241, references_wall_clock 242, replay 224,
/// tracker_process 215, tracker_force_close 213,
/// tracker_close_elapsed_hysteresis 218. The floor is half.
#[test]
fn every_entry_point_request_generator_reaches_the_working_path() {
    const FLOOR: usize = 128;
    for entry in entry_points() {
        let strategy = (entry.request)();
        let mut runner = TestRunner::deterministic();
        let accepted = (0..256)
            .filter(|_| {
                let request = strategy.new_tree(&mut runner).unwrap().current();
                call_json(entry.call, &request.to_string())["ok"] == Value::Bool(true)
            })
            .count();
        assert!(accepted >= FLOOR, "{}: {accepted} of 256", entry.name);
    }
}

type Issue = (String, String, String);

fn core_issues(
    condition_type: &str,
    params: &Value,
    auto_resolve: Option<&Value>,
    snooze: Option<&Vec<Value>>,
) -> Vec<Issue> {
    let scoped = |scope: &str, errors: Vec<ParseError>| -> Vec<Issue> {
        errors
            .into_iter()
            .map(|e| (scope.to_owned(), e.path, e.reason.code().to_owned()))
            .collect()
    };
    let mut issues = match ConditionKind::from_wire(condition_type) {
        None => vec![(
            "condition".to_owned(),
            condition_type.to_owned(),
            "unknown_kind".to_owned(),
        )],
        Some(kind) => scoped(
            "condition",
            parse_payload(kind, params)
                .err()
                .map(|e| e.0)
                .unwrap_or_default(),
        ),
    };
    if let Some(tree) = auto_resolve.filter(|v| !v.is_null()) {
        let errors = Node::parse_rooted(tree, "auto_resolve").err();
        issues.extend(scoped(
            "auto_resolve",
            errors.map(|e| e.0).unwrap_or_default(),
        ));
    }
    if let Some(conditions) = snooze.filter(|c| !c.is_empty()) {
        let node = json!({
            "type": "composite",
            "composite": { "operator": "and", "conditions": conditions },
        });
        let errors = Node::parse_rooted(&node, "snooze").err();
        issues.extend(scoped("snooze", errors.map(|e| e.0).unwrap_or_default()));
    }
    issues
}

fn broken(doc: BoxedStrategy<Value>) -> BoxedStrategy<Value> {
    (doc, mutations(0..3))
        .prop_map(|(doc, ops)| mutated(doc, &ops))
        .boxed()
}

fn validate_inputs() -> impl Strategy<Value = (String, Value, Option<Value>, Option<Vec<Value>>)> {
    let condition = prop_oneof![
        9 => select(ConditionKind::ALL.to_vec()).prop_flat_map(|kind| {
            broken(payload(kind, &tree())).prop_map(move |p| (kind.name().to_owned(), p))
        }),
        1 => ("[a-z_]{0,8}", arbitrary_json()),
    ];
    let auto_resolve = option::of(prop_oneof![
        4 => broken(tree()),
        1 => Just(Value::Null),
    ]);
    let snooze = option::of(vec(broken(tree()), 0..3));
    (condition, auto_resolve, snooze)
        .prop_map(|((kind, params), auto, snooze)| (kind, params, auto, snooze))
}

proptest! {
    /// The `validate` envelope's verdict and issues are those of the core
    /// readers on each scope it composes: the rule body, the auto-resolve
    /// tree and the snooze conditions as an `and` composite.
    #[test]
    fn validate_reports_what_the_core_readers_find_in_each_scope(
        (kind, params, auto, snooze) in validate_inputs(),
    ) {
        let mut request = json!({
            "schema_version": 1,
            "condition_type": kind,
            "condition_params": params,
        });
        if let Some(auto) = &auto {
            request["auto_resolve_params"] = auto.clone();
        }
        if let Some(snooze) = &snooze {
            request["snooze_conditions"] = Value::from(snooze.clone());
        }
        let response = call_json(nocturne_alerts_validate, &request.to_string());
        let too_deep_for_json = response["error"]
            .as_str()
            .is_some_and(|e| e.contains("recursion limit exceeded"));
        prop_assume!(!too_deep_for_json);
        prop_assert_eq!(&response["ok"], &Value::Bool(true), "{}", response);

        let expected = core_issues(&kind, &params, auto.as_ref(), snooze.as_ref());
        let reported: Vec<Issue> = response["issues"]
            .as_array()
            .unwrap()
            .iter()
            .map(|issue| {
                let text = |key: &str| issue[key].as_str().unwrap().to_owned();
                (text("scope"), text("path"), text("reason"))
            })
            .collect();
        prop_assert_eq!(&response["valid"], &Value::Bool(expected.is_empty()));
        prop_assert_eq!(reported, expected);
    }

    /// `evaluate` fed its own `timers` and `tracker` each tick returns, tick by
    /// tick, the `result` of the core engine run over the same sequence.
    #[test]
    fn evaluate_threaded_through_its_response_matches_the_core_engine(
        spec in rule_spec(),
        ticks in ticks(),
    ) {
        let wire: WireRule = serde_json::from_value(spec.wire()).unwrap();
        let rule = Rule::try_from(wire).unwrap();
        let mut engine = EngineState::new();

        let mut timers = json!({});
        let mut tracker = Value::Null;
        for tick in &ticks {
            let response = call_json(
                nocturne_alerts_evaluate,
                &json!({
                    "schema_version": 1,
                    "rule": spec.wire(),
                    "context": tick.context,
                    "now": tick.now.to_rfc3339_opts(SecondsFormat::Secs, true),
                    "timers": timers,
                    "tracker": tracker,
                })
                .to_string(),
            );
            prop_assert_eq!(&response["ok"], &Value::Bool(true), "{}", response);

            let ctx: SensorContext = serde_json::from_value(tick.context.clone()).unwrap();
            let core = evaluate_rule(&rule, &ctx, tick.now, &mut engine, true);
            prop_assert_eq!(&response["result"], &core.to_json());

            timers = response["timers"].clone();
            tracker = response["tracker"].clone();
        }
    }
}
