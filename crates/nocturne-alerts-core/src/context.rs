//! `SensorContext` as plain data: pure input to the evaluators, read from the
//! context wire format (engine-semantics.md §4). An enum name this crate does
//! not know (a host newer than it) degrades only that fact: an unknown bucket
//! reads as an absent one, an active pump mode this crate cannot name keeps
//! its entry with no mode, a state span of an unknown category is dropped, and
//! a sensor state this crate cannot name is kept as one that is not ok, a
//! forecast curve this crate cannot name is dropped, so every other leaf
//! still evaluates.

use std::collections::{BTreeMap, HashMap};

use chrono::{DateTime, NaiveDate, Utc};
use rust_decimal::Decimal;
use serde::Deserialize;
use serde_json::{Map, Number, Value};
use uuid::Uuid;

use crate::enums::{
    ForecastCurve, GlucoseBucket, PumpMode, SensorState, StateSpanCategory, TrendBucket, WireEnum,
};
use crate::model::decimal_from_number;

#[derive(Debug, Clone)]
pub struct ActiveAlertSnapshot {
    pub state: String,
    pub triggered_at: DateTime<Utc>,
    pub acknowledged_at: Option<DateTime<Utc>>,
}

#[derive(Debug, Clone)]
pub struct TempBasalSnapshot {
    pub rate: Decimal,
    pub scheduled_rate: Option<Decimal>,
    pub started_at: DateTime<Utc>,
}

/// An override, pump suspension or Do Not Disturb span; only its start is
/// read.
#[derive(Debug, Clone, Copy)]
pub struct StartedSpan {
    pub started_at: DateTime<Utc>,
}

/// One active pump-mode span. Spans of different modes overlap, and a repeated
/// mode is allowed: `pump_state` reads the latest start of the mode it names.
#[derive(Debug, Clone, Copy)]
pub struct PumpModeSnapshot {
    /// `None`: a mode this crate has no name for.
    pub mode: Option<PumpMode>,
    pub started_at: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy)]
pub struct StateSpanSnapshot {
    pub started_at: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy)]
pub struct PredictedPoint {
    pub offset_minutes: i32,
    pub mgdl: Decimal,
}

/// The newest determination of the user's loop (an oref or Loop cycle) at or
/// before `now`: when it was made, and what it said.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ApsDetermination {
    pub at: DateTime<Utc>,
    /// The glucose the loop expected at the end of its forecast, in mg/dL.
    pub eventual_mgdl: Option<Decimal>,
    /// The carbohydrate the loop said it needed, in grams. The host sends
    /// what the loop reported, and `None` where the loop reports nothing
    /// (engine-semantics.md §4).
    pub carbs_required_grams: Option<Decimal>,
}

/// Pure-data snapshot of current sensor state. No clock: `now` is supplied to
/// the engine separately.
#[derive(Debug, Clone, Default)]
pub struct SensorContext {
    pub latest_value: Option<Decimal>,
    pub latest_timestamp: Option<DateTime<Utc>>,
    /// When the host received the newest reading, which its glucose facts'
    /// age is measured from (engine-semantics.md §3); absent is
    /// `latest_timestamp`.
    pub latest_received_at: Option<DateTime<Utc>>,
    pub trend_rate: Option<Decimal>,
    pub last_reading_at: Option<DateTime<Utc>>,
    pub trend_bucket: Option<TrendBucket>,
    pub iob_units: Option<Decimal>,
    pub cob_grams: Option<Decimal>,
    pub reservoir_units: Option<Decimal>,
    /// True when `reservoir_units` is a lower bound rather than an exact reading
    /// (e.g. an Omnipod reports "50+" while the reservoir holds at least 50 units).
    pub reservoir_is_lower_bound: bool,
    pub last_site_change_at: Option<DateTime<Utc>>,
    pub last_sensor_start_at: Option<DateTime<Utc>>,
    /// The `default` forecast series: whatever the host has always sent.
    pub predictions: Vec<PredictedPoint>,
    /// The named forecast curves other than `default`; a curve the host has
    /// none of is absent.
    pub prediction_curves: BTreeMap<ForecastCurve, Vec<PredictedPoint>>,
    pub aps_determination: Option<ApsDetermination>,
    pub active_alerts: HashMap<Uuid, ActiveAlertSnapshot>,
    pub last_aps_cycle_at: Option<DateTime<Utc>>,
    pub last_aps_enacted_at: Option<DateTime<Utc>>,
    pub pump_battery_percent: Option<Decimal>,
    pub active_temp_basal: Option<TempBasalSnapshot>,
    pub uploader_battery_percent: Option<Decimal>,
    /// The CGM transmitter's latest reported readings, unscaled; each `None`
    /// until an uploader reports it.
    pub transmitter_voltage_a: Option<Decimal>,
    pub transmitter_voltage_b: Option<Decimal>,
    pub transmitter_resistance: Option<Decimal>,
    pub transmitter_days: Option<Decimal>,
    pub active_override: Option<StartedSpan>,
    pub active_pump_suspension: Option<StartedSpan>,
    pub sensitivity_ratio: Option<Decimal>,
    pub active_do_not_disturb: Option<StartedSpan>,
    pub has_ever_aps_cycled: bool,
    pub has_ever_pump_snapshot: bool,
    pub has_ever_uploader_snapshot: bool,
    pub has_ever_aps_sensitivity: bool,
    pub glucose_bucket: Option<GlucoseBucket>,
    /// The low end of the profile's target range in effect at `now`, in
    /// mg/dL: the one the host placed `glucose_bucket` with. `None` when the
    /// profile has no target range, never a default (engine-semantics.md §4).
    pub target_low: Option<Decimal>,
    /// The high end of the same range.
    pub target_high: Option<Decimal>,
    pub last_carb_at: Option<DateTime<Utc>>,
    pub last_bolus_at: Option<DateTime<Utc>>,
    pub tenant_time_zone_id: Option<String>,
    /// Every active pump-mode span, in the order the host sent them.
    pub active_pump_modes: Vec<PumpModeSnapshot>,
    /// Keyed by `(category, state)`; a `None` state means "any state of this
    /// category".
    pub active_state_spans: HashMap<(StateSpanCategory, Option<String>), StateSpanSnapshot>,
    /// The active tracker instance per tracker definition. Absent key = no
    /// active instance.
    pub active_trackers: HashMap<Uuid, TrackerReference>,
    /// Pre-computed by the enricher: a sleep session (from the sleep_sessions
    /// tables) has `StartTime <= now <= EndTime` for the tenant.
    pub sleep_session_active: bool,
    /// The sensor's state; `None` when nothing is known of it, which glucose
    /// leaves read as they would without the fact.
    pub sensor_state: Option<SensorStateSnapshot>,
}

/// The sensor's state and, where the source gives one, the instant it began.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SensorStateSnapshot {
    /// `None` for a name this crate does not know, read as a state that is
    /// not ok (engine-semantics.md §4).
    pub state: Option<SensorState>,
    pub since: Option<DateTime<Utc>>,
    /// The instant the host stops counting the report. A state whose `until`
    /// is at or before `now` is no state (engine-semantics.md §4). `None`
    /// counts for as long as the host sends it, as `no_value` does.
    pub until: Option<DateTime<Utc>>,
}

impl SensorContext {
    /// The points of the series `curve`; empty when the host sent none.
    #[must_use]
    pub fn prediction_series(&self, curve: ForecastCurve) -> &[PredictedPoint] {
        match curve {
            ForecastCurve::Default => &self.predictions,
            named => self
                .prediction_curves
                .get(&named)
                .map_or(&[], Vec::as_slice),
        }
    }

    /// The sensor's state at `now`: none when it is absent or its report has
    /// lapsed. The engine owns the lapse, so a host that evaluates late
    /// cannot keep a state its own bound has ended.
    #[must_use]
    pub fn sensor_state_at(&self, now: DateTime<Utc>) -> Option<SensorStateSnapshot> {
        self.sensor_state
            .filter(|s| s.until.is_none_or(|until| until > now))
    }

    /// When the sensor's state lapses, if that is after `now`.
    #[must_use]
    pub fn sensor_state_lapses(&self, now: DateTime<Utc>) -> Option<DateTime<Utc>> {
        self.sensor_state
            .and_then(|s| s.until)
            .filter(|&until| until > now)
    }
}

/// A tracker definition's active instance, as `tracker_age` reads it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TrackerReference {
    /// The instance's start for a duration tracker, its scheduled time for an
    /// event tracker (resolved by the enricher).
    pub reference_at: DateTime<Utc>,
    /// When the instance was recorded. A threshold that already held then does
    /// not fire for a duration instance; `None` holds nothing back.
    pub recorded_at: Option<DateTime<Utc>>,
    /// An event tracker's instance, counting to its scheduled time.
    pub scheduled: bool,
}

impl TrackerReference {
    /// A duration instance with no recorded time.
    #[must_use]
    pub fn at(reference_at: DateTime<Utc>) -> Self {
        Self {
            reference_at,
            recorded_at: None,
            scheduled: false,
        }
    }
}

// ---------------------------------------------------------------------------
// Wire format (scenario context JSON)
// ---------------------------------------------------------------------------

#[derive(Deserialize)]
struct WireContext {
    #[serde(default)]
    latest_value: Option<Number>,
    #[serde(default)]
    latest_timestamp: Option<DateTime<Utc>>,
    #[serde(default)]
    latest_received_at: Option<DateTime<Utc>>,
    #[serde(default)]
    trend_rate: Option<Number>,
    #[serde(default)]
    last_reading_at: Option<DateTime<Utc>>,
    #[serde(default)]
    trend_bucket: Option<String>,
    #[serde(default)]
    iob_units: Option<Number>,
    #[serde(default)]
    cob_grams: Option<Number>,
    #[serde(default)]
    reservoir_units: Option<Number>,
    #[serde(default)]
    reservoir_is_lower_bound: Option<bool>,
    #[serde(default)]
    last_site_change_at: Option<DateTime<Utc>>,
    #[serde(default)]
    last_sensor_start_at: Option<DateTime<Utc>>,
    #[serde(default)]
    predictions: Option<Vec<WirePrediction>>,
    #[serde(default)]
    prediction_curves: Option<BTreeMap<String, Vec<WirePrediction>>>,
    #[serde(default)]
    aps_determination: Option<WireApsDetermination>,
    #[serde(default)]
    active_alerts: Option<Vec<WireAlert>>,
    #[serde(default)]
    last_aps_cycle_at: Option<DateTime<Utc>>,
    #[serde(default)]
    last_aps_enacted_at: Option<DateTime<Utc>>,
    #[serde(default)]
    pump_battery_percent: Option<Number>,
    #[serde(default)]
    active_temp_basal: Option<WireTempBasal>,
    #[serde(default)]
    uploader_battery_percent: Option<Number>,
    #[serde(default)]
    transmitter_voltage_a: Option<Number>,
    #[serde(default)]
    transmitter_voltage_b: Option<Number>,
    #[serde(default)]
    transmitter_resistance: Option<Number>,
    #[serde(default)]
    transmitter_days: Option<Number>,
    #[serde(default)]
    active_override: Option<WireStartedSpan>,
    #[serde(default)]
    active_pump_suspension: Option<WireStartedSpan>,
    #[serde(default)]
    sensitivity_ratio: Option<Number>,
    #[serde(default)]
    active_do_not_disturb: Option<WireStartedSpan>,
    #[serde(default)]
    has_ever_aps_cycled: bool,
    #[serde(default)]
    has_ever_pump_snapshot: bool,
    #[serde(default)]
    has_ever_uploader_snapshot: bool,
    #[serde(default)]
    has_ever_aps_sensitivity: bool,
    #[serde(default)]
    glucose_bucket: Option<String>,
    #[serde(default)]
    target_low: Option<Number>,
    #[serde(default)]
    target_high: Option<Number>,
    #[serde(default)]
    last_carb_at: Option<DateTime<Utc>>,
    #[serde(default)]
    last_bolus_at: Option<DateTime<Utc>>,
    #[serde(default)]
    tenant_time_zone_id: Option<String>,
    #[serde(default)]
    active_pump_modes: Option<Vec<WirePumpMode>>,
    #[serde(default)]
    active_state_spans: Option<Vec<WireStateSpan>>,
    #[serde(default)]
    active_trackers: Option<Vec<WireTrackerReference>>,
    #[serde(default)]
    sleep_session_active: bool,
    #[serde(default)]
    sensor_state: Option<WireSensorState>,
}

#[derive(Deserialize)]
struct WireSensorState {
    state: String,
    #[serde(default)]
    since: Option<DateTime<Utc>>,
    #[serde(default)]
    until: Option<DateTime<Utc>>,
}

#[derive(Deserialize)]
struct WirePrediction {
    offset_minutes: i32,
    mgdl: Number,
}

#[derive(Deserialize)]
struct WireApsDetermination {
    at: DateTime<Utc>,
    #[serde(default)]
    eventual_mgdl: Option<Number>,
    #[serde(default)]
    carbs_required_grams: Option<Number>,
}

fn points(wire: &[WirePrediction]) -> Result<Vec<PredictedPoint>, String> {
    wire.iter()
        .map(|p| {
            Ok(PredictedPoint {
                offset_minutes: p.offset_minutes,
                mgdl: dec(&p.mgdl, "prediction mgdl")?,
            })
        })
        .collect()
}

/// The named curves of `wire`. `default` is `predictions`, so a curve of that
/// name here is not read, nor is one this crate has no member for.
fn named_curves(
    wire: Option<BTreeMap<String, Vec<WirePrediction>>>,
) -> Result<BTreeMap<ForecastCurve, Vec<PredictedPoint>>, String> {
    let mut curves = BTreeMap::new();
    for (name, series) in wire.unwrap_or_default() {
        match ForecastCurve::from_name(&name) {
            Some(ForecastCurve::Default) | None => {}
            Some(curve) => {
                curves.insert(curve, points(&series)?);
            }
        }
    }
    Ok(curves)
}

#[derive(Deserialize)]
struct WireAlert {
    alert_id: Uuid,
    state: String,
    triggered_at: DateTime<Utc>,
    #[serde(default)]
    acknowledged_at: Option<DateTime<Utc>>,
}

#[derive(Deserialize)]
struct WireTempBasal {
    rate: Number,
    #[serde(default)]
    scheduled_rate: Option<Number>,
    started_at: DateTime<Utc>,
}

#[derive(Deserialize)]
struct WireStartedSpan {
    started_at: DateTime<Utc>,
}

#[derive(Deserialize)]
struct WirePumpMode {
    mode: String,
    started_at: DateTime<Utc>,
}

#[derive(Deserialize)]
struct WireStateSpan {
    category: String,
    #[serde(default)]
    state: Option<String>,
    started_at: DateTime<Utc>,
}

#[derive(Deserialize)]
struct WireTrackerReference {
    tracker_definition_id: Uuid,
    reference_at: DateTime<Utc>,
    #[serde(default)]
    recorded_at: Option<DateTime<Utc>>,
    #[serde(default)]
    scheduled: bool,
}

fn dec(n: &Number, what: &str) -> Result<Decimal, String> {
    decimal_from_number(n).ok_or_else(|| format!("invalid decimal for {what}"))
}

fn opt_dec(n: Option<&Number>, what: &str) -> Result<Option<Decimal>, String> {
    n.map(|n| dec(n, what)).transpose()
}

/// Rejects an instant outside 0001-01-01 up to, not including, 10000-01-01
/// UTC, the host `DateTime` domain, whose elapsed-time arithmetic has no host
/// counterpart outside it. The error names the field, never the value.
pub fn check_timestamp(at: DateTime<Utc>, field: &str) -> Result<DateTime<Utc>, String> {
    let min = NaiveDate::from_ymd_opt(1, 1, 1).and_then(|d| d.and_hms_opt(0, 0, 0));
    let end = NaiveDate::from_ymd_opt(10_000, 1, 1).and_then(|d| d.and_hms_opt(0, 0, 0));
    let in_range = match (min, end) {
        (Some(min), Some(end)) => (min.and_utc()..end.and_utc()).contains(&at),
        _ => false,
    };
    if in_range {
        Ok(at)
    } else {
        Err(format!("{field} is outside the supported timestamp range"))
    }
}

pub(crate) fn opt_ts(
    at: Option<DateTime<Utc>>,
    field: &str,
) -> Result<Option<DateTime<Utc>>, String> {
    at.map(|at| check_timestamp(at, field)).transpose()
}

fn started(span: Option<WireStartedSpan>, field: &str) -> Result<Option<StartedSpan>, String> {
    span.map(|s| {
        Ok(StartedSpan {
            started_at: check_timestamp(s.started_at, field)?,
        })
    })
    .transpose()
}

impl<'de> Deserialize<'de> for SensorContext {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let value = Value::deserialize(deserializer)?;
        let w = WireContext::deserialize(&value)
            .map_err(|_| serde::de::Error::custom(wire_error(&value)))?;
        Self::try_from_wire(w).map_err(serde::de::Error::custom)
    }
}

/// Names the top-level context field that fails to deserialise. serde's own
/// messages quote the offending value, which here is health data bound for
/// host logs.
fn wire_error(value: &Value) -> String {
    let Value::Object(fields) = value else {
        return "context must be a JSON object".into();
    };
    fields
        .iter()
        .find(|(name, field)| {
            let single = Value::Object(Map::from_iter([((*name).clone(), (*field).clone())]));
            WireContext::deserialize(&single).is_err()
        })
        .map_or_else(
            || "invalid context".into(),
            |(name, _)| format!("invalid value for context field {name}"),
        )
}

impl SensorContext {
    fn try_from_wire(w: WireContext) -> Result<Self, String> {
        let mut active_alerts = HashMap::new();
        for a in w.active_alerts.unwrap_or_default() {
            active_alerts.insert(
                a.alert_id,
                ActiveAlertSnapshot {
                    state: a.state,
                    triggered_at: check_timestamp(a.triggered_at, "active_alerts.triggered_at")?,
                    acknowledged_at: opt_ts(a.acknowledged_at, "active_alerts.acknowledged_at")?,
                },
            );
        }

        let mut active_trackers = HashMap::new();
        for t in w.active_trackers.unwrap_or_default() {
            active_trackers.insert(
                t.tracker_definition_id,
                TrackerReference {
                    reference_at: check_timestamp(t.reference_at, "active_trackers.reference_at")?,
                    recorded_at: opt_ts(t.recorded_at, "active_trackers.recorded_at")?,
                    scheduled: t.scheduled,
                },
            );
        }

        let mut active_pump_modes = Vec::new();
        for p in w.active_pump_modes.unwrap_or_default() {
            active_pump_modes.push(PumpModeSnapshot {
                mode: PumpMode::from_name(&p.mode),
                started_at: check_timestamp(p.started_at, "active_pump_modes.started_at")?,
            });
        }

        let mut active_state_spans = HashMap::new();
        for s in w.active_state_spans.unwrap_or_default() {
            let started_at = check_timestamp(s.started_at, "active_state_spans.started_at")?;
            if let Some(category) = StateSpanCategory::from_name(&s.category) {
                active_state_spans.insert((category, s.state), StateSpanSnapshot { started_at });
            }
        }

        Ok(SensorContext {
            latest_value: opt_dec(w.latest_value.as_ref(), "latest_value")?,
            latest_timestamp: opt_ts(w.latest_timestamp, "latest_timestamp")?,
            latest_received_at: opt_ts(w.latest_received_at, "latest_received_at")?,
            trend_rate: opt_dec(w.trend_rate.as_ref(), "trend_rate")?,
            last_reading_at: opt_ts(w.last_reading_at, "last_reading_at")?,
            trend_bucket: w.trend_bucket.and_then(|s| TrendBucket::from_name(&s)),
            iob_units: opt_dec(w.iob_units.as_ref(), "iob_units")?,
            cob_grams: opt_dec(w.cob_grams.as_ref(), "cob_grams")?,
            reservoir_units: opt_dec(w.reservoir_units.as_ref(), "reservoir_units")?,
            reservoir_is_lower_bound: w.reservoir_is_lower_bound.unwrap_or(false),
            last_site_change_at: opt_ts(w.last_site_change_at, "last_site_change_at")?,
            last_sensor_start_at: opt_ts(w.last_sensor_start_at, "last_sensor_start_at")?,
            predictions: points(&w.predictions.unwrap_or_default())?,
            prediction_curves: named_curves(w.prediction_curves)?,
            aps_determination: w
                .aps_determination
                .map(|d| {
                    Ok::<_, String>(ApsDetermination {
                        at: check_timestamp(d.at, "aps_determination.at")?,
                        eventual_mgdl: opt_dec(
                            d.eventual_mgdl.as_ref(),
                            "aps_determination.eventual_mgdl",
                        )?,
                        carbs_required_grams: opt_dec(
                            d.carbs_required_grams.as_ref(),
                            "aps_determination.carbs_required_grams",
                        )?,
                    })
                })
                .transpose()?,
            active_alerts,
            last_aps_cycle_at: opt_ts(w.last_aps_cycle_at, "last_aps_cycle_at")?,
            last_aps_enacted_at: opt_ts(w.last_aps_enacted_at, "last_aps_enacted_at")?,
            pump_battery_percent: opt_dec(w.pump_battery_percent.as_ref(), "pump_battery_percent")?,
            active_temp_basal: w
                .active_temp_basal
                .map(|tb| {
                    Ok::<_, String>(TempBasalSnapshot {
                        rate: dec(&tb.rate, "temp basal rate")?,
                        scheduled_rate: opt_dec(tb.scheduled_rate.as_ref(), "scheduled_rate")?,
                        started_at: check_timestamp(tb.started_at, "active_temp_basal.started_at")?,
                    })
                })
                .transpose()?,
            uploader_battery_percent: opt_dec(
                w.uploader_battery_percent.as_ref(),
                "uploader_battery_percent",
            )?,
            transmitter_voltage_a: opt_dec(
                w.transmitter_voltage_a.as_ref(),
                "transmitter_voltage_a",
            )?,
            transmitter_voltage_b: opt_dec(
                w.transmitter_voltage_b.as_ref(),
                "transmitter_voltage_b",
            )?,
            transmitter_resistance: opt_dec(
                w.transmitter_resistance.as_ref(),
                "transmitter_resistance",
            )?,
            transmitter_days: opt_dec(w.transmitter_days.as_ref(), "transmitter_days")?,
            active_override: started(w.active_override, "active_override.started_at")?,
            active_pump_suspension: started(
                w.active_pump_suspension,
                "active_pump_suspension.started_at",
            )?,
            sensitivity_ratio: opt_dec(w.sensitivity_ratio.as_ref(), "sensitivity_ratio")?,
            active_do_not_disturb: started(
                w.active_do_not_disturb,
                "active_do_not_disturb.started_at",
            )?,
            has_ever_aps_cycled: w.has_ever_aps_cycled,
            has_ever_pump_snapshot: w.has_ever_pump_snapshot,
            has_ever_uploader_snapshot: w.has_ever_uploader_snapshot,
            has_ever_aps_sensitivity: w.has_ever_aps_sensitivity,
            glucose_bucket: w.glucose_bucket.and_then(|s| GlucoseBucket::from_name(&s)),
            target_low: opt_dec(w.target_low.as_ref(), "target_low")?,
            target_high: opt_dec(w.target_high.as_ref(), "target_high")?,
            last_carb_at: opt_ts(w.last_carb_at, "last_carb_at")?,
            last_bolus_at: opt_ts(w.last_bolus_at, "last_bolus_at")?,
            tenant_time_zone_id: w.tenant_time_zone_id,
            active_pump_modes,
            active_state_spans,
            active_trackers,
            sleep_session_active: w.sleep_session_active,
            sensor_state: w
                .sensor_state
                .map(|s| {
                    Ok::<_, String>(SensorStateSnapshot {
                        state: SensorState::from_name(&s.state),
                        since: opt_ts(s.since, "sensor_state.since")?,
                        until: opt_ts(s.until, "sensor_state.until")?,
                    })
                })
                .transpose()?,
        })
    }
}

#[cfg(test)]
mod tests {
    #![allow(clippy::expect_used)]

    use serde_json::json;

    use super::*;

    fn read(value: serde_json::Value) -> SensorContext {
        serde_json::from_value(value).expect("a context")
    }

    /// An engine reads a context member it does not know as absent, so an engine older than
    /// `sensor_state` evaluates a context that carries one as a context with no sensor state.
    #[test]
    fn a_member_the_engine_does_not_know_is_ignored() {
        let with = read(json!({ "latest_value": 60, "sensor_state_v2": { "state": "error" } }));
        let without = read(json!({ "latest_value": 60 }));
        assert_eq!(format!("{with:?}"), format!("{without:?}"));
    }

    fn mgdl_at(points: &[PredictedPoint]) -> Vec<(i32, String)> {
        points
            .iter()
            .map(|p| (p.offset_minutes, p.mgdl.to_string()))
            .collect()
    }

    /// The old wire shape, one array, is the `default` series, and reads as it did.
    #[test]
    fn the_one_array_is_the_default_series() {
        let ctx = read(json!({ "predictions": [{ "offset_minutes": 5, "mgdl": 80 }] }));
        assert_eq!(
            mgdl_at(ctx.prediction_series(ForecastCurve::Default)),
            [(5, "80".to_owned())]
        );
        assert!(ctx.prediction_curves.is_empty());
        assert!(ctx.prediction_series(ForecastCurve::Iob).is_empty());
    }

    /// `default` is `predictions`; a curve under that name in `prediction_curves`, or under a
    /// name the crate has no member for, is not read, so it cannot reach an unnamed `predicted`.
    #[test]
    fn named_curves_read_beside_the_default_and_an_unknown_name_is_dropped() {
        let ctx = read(json!({
            "predictions": [{ "offset_minutes": 5, "mgdl": 80 }],
            "prediction_curves": {
                "cob": [{ "offset_minutes": 5, "mgdl": 120 }],
                "default": [{ "offset_minutes": 5, "mgdl": 1 }],
                "ar2": [{ "offset_minutes": 5, "mgdl": 2 }]
            }
        }));
        assert_eq!(
            mgdl_at(ctx.prediction_series(ForecastCurve::Default)),
            [(5, "80".to_owned())]
        );
        assert_eq!(
            mgdl_at(ctx.prediction_series(ForecastCurve::Cob)),
            [(5, "120".to_owned())]
        );
        assert_eq!(
            ctx.prediction_curves.keys().copied().collect::<Vec<_>>(),
            [ForecastCurve::Cob]
        );
    }

    #[test]
    fn a_determination_reads_with_either_value_absent() {
        let at = "2026-01-05T12:00:00Z";
        assert_eq!(
            read(json!({ "aps_determination": { "at": at, "eventual_mgdl": 95.5 } }))
                .aps_determination,
            Some(ApsDetermination {
                at: at.parse().expect("an instant"),
                eventual_mgdl: Some(Decimal::new(955, 1)),
                carbs_required_grams: None,
            })
        );
        assert_eq!(read(json!({})).aps_determination, None);
    }

    #[test]
    fn a_context_without_the_member_has_no_sensor_state() {
        assert_eq!(read(json!({})).sensor_state, None);
        assert_eq!(read(json!({ "sensor_state": null })).sensor_state, None);
    }

    #[test]
    fn a_state_reads_with_its_start_and_an_unknown_name_keeps_the_entry() {
        let at = "2026-01-05T12:00:00Z";
        assert_eq!(
            read(json!({ "sensor_state": { "state": "warming_up", "since": at } })).sensor_state,
            Some(SensorStateSnapshot {
                state: Some(SensorState::WarmingUp),
                since: Some(at.parse().expect("an instant")),
                until: None,
            })
        );
        assert_eq!(
            read(json!({ "sensor_state": { "state": "rebooting" } })).sensor_state,
            Some(SensorStateSnapshot {
                state: None,
                since: None,
                until: None,
            })
        );
    }
}
