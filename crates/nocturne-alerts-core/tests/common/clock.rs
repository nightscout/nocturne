//! Contexts that hold every anchor a wall-clock leaf measures from, for the
//! next-change property (engine-semantics.md §5.3).

use chrono::{DateTime, TimeDelta, Utc};
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::enums::{
    AlertStateKind, GlucoseBucket, PumpMode, StateSpanCategory, TrendBucket, WireEnum,
};

use crate::generators::{object, word};

/// The alert every generated `alert_state` leaf and context names.
pub(crate) const ALERT_ID: Uuid = Uuid::from_u128(2);

/// The tracker every generated `tracker_age` leaf and context names.
pub(crate) const TRACKER_ID: Uuid = Uuid::from_u128(3);

/// `doc` with every `alert_id` and `tracker_definition_id` naming
/// [`ALERT_ID`] and [`TRACKER_ID`], so its leaves meet the context.
pub(crate) fn naming_the_context(mut doc: Value) -> Value {
    fn rename(v: &mut Value) {
        match v {
            Value::Object(fields) => {
                for (key, item) in fields.iter_mut() {
                    match key.as_str() {
                        "alert_id" => *item = ALERT_ID.to_string().into(),
                        "tracker_definition_id" => *item = TRACKER_ID.to_string().into(),
                        _ => rename(item),
                    }
                }
            }
            Value::Array(items) => items.iter_mut().for_each(rename),
            _ => {}
        }
    }
    rename(&mut doc);
    doc
}

/// A member's wire name, or one the crate has no member for a quarter of the
/// time: a host newer than the crate sends them.
pub(crate) fn word_or_unrecognised<E: WireEnum>(
    unrecognised: &'static str,
) -> BoxedStrategy<&'static str> {
    prop_oneof![3 => word::<E>(), 1 => Just(unrecognised)].boxed()
}

/// An instant `seconds` before `now`, or none; a negative age is in the
/// future.
fn anchor(now: DateTime<Utc>, seconds: Option<i64>) -> Option<Value> {
    seconds.map(|s| Value::from((now - TimeDelta::seconds(s)).to_rfc3339()))
}

/// Ages in seconds, up to nine days back and an hour ahead.
fn age() -> BoxedStrategy<Option<i64>> {
    option::of(prop_oneof![-3600..=3600i64, -3600..=777_600i64]).boxed()
}

/// A context holding every anchor a wall-clock leaf measures from, each
/// present or not, at ages around `now`.
pub(crate) fn clock_context(now: DateTime<Utc>) -> BoxedStrategy<Value> {
    let categories: Vec<&'static str> = StateSpanCategory::ALL
        .iter()
        .filter(|c| **c != StateSpanCategory::PumpMode)
        .map(|c| c.name())
        .chain(["NotACategory"])
        .collect();
    let zone = select(vec![
        "UTC",
        "Australia/Sydney",
        "America/New_York",
        "Europe/London",
    ]);
    (
        (age(), age(), age(), age(), age(), age(), age()),
        (age(), age(), age(), age(), age(), age()),
        (
            option::of(40..300i32),
            any::<bool>(),
            any::<bool>(),
            word_or_unrecognised::<PumpMode>("Turbo"),
            select(categories),
            word::<AlertStateKind>(),
            option::of(zone),
        ),
        (
            option::of(word_or_unrecognised::<TrendBucket>("sideways")),
            option::of(word_or_unrecognised::<GlucoseBucket>("off_the_chart")),
        ),
    )
        .prop_map(
            move |(
                (reading, site, sensor, aps, enacted, carb, bolus),
                (suspension, override_, dnd, pump_state, span, alert),
                (glucose, aps_cycled, pump_snapshot, mode, category, alert_state, zone),
                (trend, glucose_bucket),
            )| {
                let at = |s| anchor(now, s);
                let started = |s| at(s).map(|at| json!({ "started_at": at }));
                object(vec![
                    ("latest_value", glucose.map(Value::from)),
                    ("latest_timestamp", at(reading)),
                    ("last_reading_at", at(reading)),
                    ("last_site_change_at", at(site)),
                    ("last_sensor_start_at", at(sensor)),
                    ("last_aps_cycle_at", at(aps)),
                    ("last_aps_enacted_at", at(enacted)),
                    ("has_ever_aps_cycled", Some(aps_cycled.into())),
                    ("has_ever_pump_snapshot", Some(pump_snapshot.into())),
                    ("last_carb_at", at(carb)),
                    ("last_bolus_at", at(bolus)),
                    ("active_pump_suspension", started(suspension)),
                    ("active_override", started(override_)),
                    (
                        "active_do_not_disturb",
                        at(dnd).map(|at| json!({ "started_at": at, "source": "manual" })),
                    ),
                    (
                        "active_pump_state",
                        at(pump_state).map(|at| json!({ "mode": mode, "started_at": at })),
                    ),
                    (
                        "active_state_spans",
                        at(span).map(
                            |at| json!([{ "category": category, "state": null, "started_at": at }]),
                        ),
                    ),
                    (
                        "active_alerts",
                        at(alert).map(|at| {
                            json!([{
                                "alert_id": ALERT_ID, "state": alert_state,
                                "triggered_at": at, "acknowledged_at": at
                            }])
                        }),
                    ),
                    (
                        "active_trackers",
                        at(site).map(|reference| {
                            json!([{
                                "tracker_definition_id": TRACKER_ID,
                                "reference_at": reference,
                                "recorded_at": at(sensor)
                            }])
                        }),
                    ),
                    ("tenant_time_zone_id", zone.map(Value::from)),
                    ("trend_bucket", trend.map(Value::from)),
                    ("glucose_bucket", glucose_bucket.map(Value::from)),
                ])
            },
        )
        .boxed()
}
