//! A context that fills every `SensorContext` field, each present about half
//! the time, with instants at whole-second ages around `generators::base()`
//! and quantities in halves, so that a leaf's operand meets its fact exactly
//! now and then.

use chrono::TimeDelta;
use proptest::collection::vec;
use proptest::option;
use proptest::prelude::*;
use proptest::sample::select;
use serde_json::{Value, json};
use uuid::Uuid;

use nocturne_alerts_core::enums::{
    GlucoseBucket, PumpMode, SensorState, StateSpanCategory, TrendBucket, WireEnum,
};

use crate::generators::{base, object, word};

pub(crate) const ALERT_IDS: [Uuid; 2] = [Uuid::from_u128(11), Uuid::from_u128(12)];

pub(crate) const TRACKER_IDS: [Uuid; 2] = [Uuid::from_u128(21), Uuid::from_u128(22)];

pub(crate) const TENANT_ZONES: [&str; 7] = [
    "UTC",
    "Australia/Sydney",
    "America/New_York",
    "Pacific/Kiritimati",
    "ETC/GMT-2",
    "us/pacific",
    "Not/AZone",
];

pub(crate) const UNRECOGNISED_MODE: &str = "Turbo";
pub(crate) const UNRECOGNISED_CATEGORY: &str = "NotACategory";
pub(crate) const UNRECOGNISED_TREND: &str = "sideways";
pub(crate) const UNRECOGNISED_GLUCOSE: &str = "off_the_chart";
pub(crate) const UNRECOGNISED_SENSOR_STATE: &str = "rebooting";

fn half_present<T: std::fmt::Debug + Clone + 'static>(
    value: impl Strategy<Value = T> + 'static,
) -> BoxedStrategy<Option<T>> {
    option::weighted(0.5, value).boxed()
}

/// A quantity from -2 to 2 in halves.
pub(crate) fn quantity() -> BoxedStrategy<Value> {
    (-4..=4i32)
        .prop_map(|halves| Value::from(f64::from(halves) / 2.0))
        .boxed()
}

fn instant(seconds_ago: i64) -> Value {
    Value::from((base() - TimeDelta::seconds(seconds_ago)).to_rfc3339())
}

/// Seconds before `base()` for an anchor measured in units of `unit` seconds:
/// every half unit and every tenth of one up to five units back, a second either side of a
/// whole unit, and now and then ahead of the clock around the 5 minute tolerance.
pub(crate) fn age_in(unit: i64) -> BoxedStrategy<i64> {
    let half = unit / 2;
    prop_oneof![
        6 => (0..=10i64).prop_map(move |n| n * half),
        3 => ((1..=5i64), select(vec![-1i64, 1])).prop_map(move |(n, d)| n * unit + d),
        3 => (0..=50i64).prop_map(move |n| n * unit / 10),
        1 => select(vec![-1i64, -299, -300, -301, -3600]),
    ]
    .boxed()
}

fn started_at() -> BoxedStrategy<Value> {
    age_in(60)
        .prop_map(|a| json!({ "started_at": instant(a) }))
        .boxed()
}

fn at(unit: i64) -> BoxedStrategy<Value> {
    age_in(unit).prop_map(instant).boxed()
}

/// A reading's age: every half minute across the default freshness bound of
/// 11 minutes, exactly at it and a second either side, and a little ahead.
fn reading_at() -> BoxedStrategy<Value> {
    prop_oneof![
        6 => (0..=50i64).prop_map(|n| n * 30),
        3 => select(vec![659i64, 660, 661]),
        1 => select(vec![-1i64, -299, -300, -301]),
    ]
    .prop_map(instant)
    .boxed()
}

fn named<E: WireEnum>(unrecognised: &'static str) -> BoxedStrategy<Value> {
    prop_oneof![3 => word::<E>(), 1 => Just(unrecognised)]
        .prop_map(Value::from)
        .boxed()
}

fn predictions() -> BoxedStrategy<Value> {
    vec(
        (-2..=12i32, quantity())
            .prop_map(|(offset, mgdl)| json!({ "offset_minutes": offset, "mgdl": mgdl })),
        0..=3,
    )
    .prop_map(Value::from)
    .boxed()
}

pub(crate) const UNRECOGNISED_CURVE: &str = "ar2";

/// Named curves, each present about half the time, now and then one under
/// `default` or under a name the crate has no member for, which it does not
/// read.
fn prediction_curves() -> BoxedStrategy<Value> {
    let names = ["iob", "cob", "uam", "zt", "default", UNRECOGNISED_CURVE];
    vec(half_present(predictions()), names.len())
        .prop_map(move |series| {
            object(
                names
                    .iter()
                    .copied()
                    .zip(series)
                    .collect::<Vec<(&str, Option<Value>)>>(),
            )
        })
        .boxed()
}

/// A determination of any age around the default freshness bound, each value
/// present about half the time.
fn aps_determination() -> BoxedStrategy<Value> {
    (
        reading_at(),
        half_present(quantity()),
        half_present(quantity()),
    )
        .prop_map(|(at, eventual, carbs)| {
            object(vec![
                ("at", Some(at)),
                ("eventual_mgdl", eventual),
                ("carbs_required_grams", carbs),
            ])
        })
        .boxed()
}

fn alerts() -> BoxedStrategy<Value> {
    let alert = (
        select(vec!["firing", "Firing", "resolved"]),
        age_in(60),
        option::weighted(0.5, age_in(60)),
    );
    (
        option::weighted(0.6, alert.clone()),
        option::weighted(0.6, alert),
    )
        .prop_map(|(first, second)| {
            let entries: Vec<Value> = [first, second]
                .into_iter()
                .zip(ALERT_IDS)
                .filter_map(|(alert, id)| {
                    let (state, triggered, acknowledged) = alert?;
                    Some(object(vec![
                        ("alert_id", Some(id.to_string().into())),
                        ("state", Some(state.into())),
                        ("triggered_at", Some(instant(triggered))),
                        ("acknowledged_at", acknowledged.map(instant)),
                    ]))
                })
                .collect();
            Value::from(entries)
        })
        .boxed()
}

fn temp_basal() -> BoxedStrategy<Value> {
    (quantity(), option::weighted(0.7, quantity()), age_in(60))
        .prop_map(|(rate, scheduled, started)| {
            object(vec![
                ("rate", Some(rate)),
                ("scheduled_rate", scheduled),
                ("started_at", Some(instant(started))),
            ])
        })
        .boxed()
}

/// Active pump-mode spans, at least `min` of them, up to three.
pub(crate) fn pump_modes(min: usize) -> BoxedStrategy<Value> {
    vec(
        (named::<PumpMode>(UNRECOGNISED_MODE), age_in(60))
            .prop_map(|(mode, started)| json!({ "mode": mode, "started_at": instant(started) })),
        min..=3,
    )
    .prop_map(Value::from)
    .boxed()
}

fn state_spans() -> BoxedStrategy<Value> {
    let categories: Vec<&'static str> = StateSpanCategory::ALL
        .iter()
        .filter(|c| **c != StateSpanCategory::PumpMode)
        .map(|c| c.name())
        .chain([UNRECOGNISED_CATEGORY])
        .collect();
    vec(
        (
            select(categories),
            option::of(select(vec!["a", "b"])),
            age_in(60),
        )
            .prop_map(|(category, state, started)| {
                json!({ "category": category, "state": state, "started_at": instant(started) })
            }),
        0..=3,
    )
    .prop_map(Value::from)
    .boxed()
}

/// The sensor's state, a name the crate has no member for one time in four,
/// with its start half the time, and half the time an `until` within ten
/// minutes either side of now, exactly now now and then.
pub(crate) fn sensor_state() -> BoxedStrategy<Value> {
    (
        named::<SensorState>(UNRECOGNISED_SENSOR_STATE),
        option::weighted(0.5, age_in(60)),
        option::weighted(0.5, (-20..=20i64).prop_map(|n| n * 30)),
    )
        .prop_map(|(state, since, until)| {
            object(vec![
                ("state", Some(state)),
                ("since", since.map(instant)),
                ("until", until.map(instant)),
            ])
        })
        .boxed()
}

fn trackers() -> BoxedStrategy<Value> {
    let tracker = (age_in(60), option::weighted(0.5, age_in(60)), any::<bool>());
    (
        option::weighted(0.6, tracker.clone()),
        option::weighted(0.6, tracker),
    )
        .prop_map(|(first, second)| {
            let entries: Vec<Value> = [first, second]
                .into_iter()
                .zip(TRACKER_IDS)
                .filter_map(|(tracker, id)| {
                    let (reference, recorded, scheduled) = tracker?;
                    Some(object(vec![
                        ("tracker_definition_id", Some(id.to_string().into())),
                        ("reference_at", Some(instant(reference))),
                        ("recorded_at", recorded.map(instant)),
                        ("scheduled", Some(scheduled.into())),
                    ]))
                })
                .collect();
            Value::from(entries)
        })
        .boxed()
}

/// A context with every field generated: an option field is present half the
/// time, a flag is set half the time, and a named fact is a name the crate has
/// no member for one time in four.
pub(crate) fn full_context() -> BoxedStrategy<Value> {
    let readings = (
        half_present(quantity()),
        half_present(reading_at()),
        half_present(reading_at()),
        half_present(quantity()),
        half_present(reading_at()),
        half_present(named::<TrendBucket>(UNRECOGNISED_TREND)),
        half_present(named::<GlucoseBucket>(UNRECOGNISED_GLUCOSE)),
        half_present(predictions()),
    );
    let insulin = (
        half_present(quantity()),
        half_present(quantity()),
        half_present(quantity()),
        any::<bool>(),
        half_present(temp_basal()),
        half_present(at(60)),
        half_present(at(60)),
    );
    let devices = (
        half_present(at(3600)),
        half_present(at(86_400)),
        half_present(at(60)),
        half_present(at(60)),
        half_present(quantity()),
        half_present(quantity()),
        half_present(quantity()),
    );
    let transmitter = (
        half_present(quantity()),
        half_present(quantity()),
        half_present(quantity()),
        half_present(quantity()),
    );
    let spans = (
        half_present(started_at()),
        half_present(started_at()),
        half_present(started_at()),
        half_present(pump_modes(0)),
        half_present(state_spans()),
        half_present(alerts()),
        half_present(trackers()),
    );
    let flags = (
        any::<bool>(),
        any::<bool>(),
        any::<bool>(),
        any::<bool>(),
        any::<bool>(),
    );
    (
        readings,
        insulin,
        devices,
        transmitter,
        spans,
        flags,
        half_present(select(TENANT_ZONES.to_vec())),
        half_present(sensor_state()),
        half_present(prediction_curves()),
        half_present(aps_determination()),
        (half_present(quantity()), half_present(quantity())),
    )
        .prop_map(
            |(
                (value, timestamp, received, rate, last_reading, trend, bucket, predictions),
                (iob, cob, reservoir, lower_bound, temp, carb, bolus),
                (site, sensor, cycle, enacted, pump_battery, uploader_battery, sensitivity),
                (voltage_a, voltage_b, resistance, days),
                (suspension, override_, dnd, pump_modes, state_spans, alerts, trackers),
                (aps_cycled, pump_snapshot, uploader_snapshot, aps_sensitivity, asleep),
                zone,
                sensor_state,
                curves,
                determination,
                (target_low, target_high),
            )| {
                object(vec![
                    ("latest_value", value),
                    ("latest_timestamp", timestamp),
                    ("latest_received_at", received),
                    ("trend_rate", rate),
                    ("last_reading_at", last_reading),
                    ("trend_bucket", trend),
                    ("iob_units", iob),
                    ("cob_grams", cob),
                    ("reservoir_units", reservoir),
                    ("reservoir_is_lower_bound", Some(lower_bound.into())),
                    ("last_site_change_at", site),
                    ("last_sensor_start_at", sensor),
                    ("predictions", predictions),
                    ("prediction_curves", curves),
                    ("aps_determination", determination),
                    ("active_alerts", alerts),
                    ("last_aps_cycle_at", cycle),
                    ("last_aps_enacted_at", enacted),
                    ("pump_battery_percent", pump_battery),
                    ("active_temp_basal", temp),
                    ("uploader_battery_percent", uploader_battery),
                    ("transmitter_voltage_a", voltage_a),
                    ("transmitter_voltage_b", voltage_b),
                    ("transmitter_resistance", resistance),
                    ("transmitter_days", days),
                    ("active_override", override_),
                    ("active_pump_suspension", suspension),
                    ("sensitivity_ratio", sensitivity),
                    ("active_do_not_disturb", dnd),
                    ("has_ever_aps_cycled", Some(aps_cycled.into())),
                    ("has_ever_pump_snapshot", Some(pump_snapshot.into())),
                    ("has_ever_uploader_snapshot", Some(uploader_snapshot.into())),
                    ("has_ever_aps_sensitivity", Some(aps_sensitivity.into())),
                    ("glucose_bucket", bucket),
                    ("target_low", target_low),
                    ("target_high", target_high),
                    ("last_carb_at", carb),
                    ("last_bolus_at", bolus),
                    ("tenant_time_zone_id", zone.map(Value::from)),
                    ("active_pump_modes", pump_modes),
                    ("active_state_spans", state_spans),
                    ("active_trackers", trackers),
                    ("sleep_session_active", Some(asleep.into())),
                    ("sensor_state", sensor_state),
                ])
            },
        )
        .boxed()
}
