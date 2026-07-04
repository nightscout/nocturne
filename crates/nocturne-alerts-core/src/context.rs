//! `SensorContext` as plain data: pure input to the evaluators, deserialisable
//! from the corpus scenario context wire format (see `ScenarioModels.cs`).
//! Enum-valued facts are stored as C# enum ordinals so payload comparisons are
//! direct integer equality.

use std::collections::HashMap;

use chrono::{DateTime, Utc};
use rust_decimal::Decimal;
use serde::Deserialize;
use serde_json::Number;
use uuid::Uuid;

use crate::model::{
    GLUCOSE_BUCKET_NAMES, PUMP_MODE_NAMES, STATE_SPAN_CATEGORY_NAMES, TREND_BUCKET_NAMES,
    decimal_from_number, enum_ordinal,
};

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

/// `OverrideSnapshot` / `PumpSuspensionSnapshot` / `DoNotDisturbSnapshot` —
/// the evaluators only read `StartedAt`.
#[derive(Debug, Clone, Copy)]
pub struct StartedSpan {
    pub started_at: DateTime<Utc>,
}

#[derive(Debug, Clone, Copy)]
pub struct PumpStateSnapshot {
    /// `PumpModeState` ordinal.
    pub mode: i64,
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

/// Pure-data snapshot of current sensor state. No clock: `now` is supplied to
/// the engine separately.
#[derive(Debug, Clone, Default)]
pub struct SensorContext {
    pub latest_value: Option<Decimal>,
    pub latest_timestamp: Option<DateTime<Utc>>,
    pub trend_rate: Option<Decimal>,
    pub last_reading_at: Option<DateTime<Utc>>,
    /// `TrendBucket` ordinal (0 unknown … 5 falling_fast).
    pub trend_bucket: Option<i64>,
    pub iob_units: Option<Decimal>,
    pub cob_grams: Option<Decimal>,
    pub reservoir_units: Option<Decimal>,
    /// True when `reservoir_units` is a lower bound rather than an exact reading
    /// (e.g. an Omnipod reports "50+" while the reservoir holds at least 50 units).
    pub reservoir_is_lower_bound: bool,
    pub last_site_change_at: Option<DateTime<Utc>>,
    pub last_sensor_start_at: Option<DateTime<Utc>>,
    pub predictions: Vec<PredictedPoint>,
    pub active_alerts: HashMap<Uuid, ActiveAlertSnapshot>,
    pub last_aps_cycle_at: Option<DateTime<Utc>>,
    pub last_aps_enacted_at: Option<DateTime<Utc>>,
    pub pump_battery_percent: Option<Decimal>,
    pub active_temp_basal: Option<TempBasalSnapshot>,
    pub uploader_battery_percent: Option<Decimal>,
    pub active_override: Option<StartedSpan>,
    pub active_pump_suspension: Option<StartedSpan>,
    pub sensitivity_ratio: Option<Decimal>,
    pub active_do_not_disturb: Option<StartedSpan>,
    pub has_ever_aps_cycled: bool,
    pub has_ever_pump_snapshot: bool,
    pub has_ever_uploader_snapshot: bool,
    pub has_ever_aps_sensitivity: bool,
    /// `GlucoseBucket` ordinal (0 very_low … 5 very_high).
    pub glucose_bucket: Option<i64>,
    pub last_carb_at: Option<DateTime<Utc>>,
    pub last_bolus_at: Option<DateTime<Utc>>,
    pub tenant_time_zone_id: Option<String>,
    pub active_pump_state: Option<PumpStateSnapshot>,
    /// Keyed by `(StateSpanCategory ordinal, state)`; a `None` state means
    /// "any state of this category" (the enricher loads that exact key shape).
    pub active_state_spans: HashMap<(i64, Option<String>), StateSpanSnapshot>,
    /// Reference timestamp of the active tracker instance per tracker
    /// definition: start time for duration trackers, scheduled time for event
    /// trackers (resolved by the enricher). Absent key = no active instance.
    pub active_trackers: HashMap<Uuid, DateTime<Utc>>,
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
    last_carb_at: Option<DateTime<Utc>>,
    #[serde(default)]
    last_bolus_at: Option<DateTime<Utc>>,
    #[serde(default)]
    tenant_time_zone_id: Option<String>,
    #[serde(default)]
    active_pump_state: Option<WirePumpState>,
    #[serde(default)]
    active_state_spans: Option<Vec<WireStateSpan>>,
    #[serde(default)]
    active_trackers: Option<Vec<WireTrackerReference>>,
}

#[derive(Deserialize)]
struct WirePrediction {
    offset_minutes: i32,
    mgdl: Number,
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
struct WirePumpState {
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
}

fn dec(n: &Number, what: &str) -> Result<Decimal, String> {
    decimal_from_number(n).ok_or_else(|| format!("invalid decimal for {what}: {n}"))
}

fn opt_dec(n: Option<&Number>, what: &str) -> Result<Option<Decimal>, String> {
    n.map(|n| dec(n, what)).transpose()
}

fn ord(names: &[&str], s: &str, what: &str) -> Result<i64, String> {
    enum_ordinal(names, s).ok_or_else(|| format!("unknown {what} wire value: {s}"))
}

impl<'de> Deserialize<'de> for SensorContext {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let w = WireContext::deserialize(deserializer)?;
        Self::try_from_wire(w).map_err(serde::de::Error::custom)
    }
}

impl SensorContext {
    fn try_from_wire(w: WireContext) -> Result<Self, String> {
        let mut active_alerts = HashMap::new();
        for a in w.active_alerts.unwrap_or_default() {
            active_alerts.insert(
                a.alert_id,
                ActiveAlertSnapshot {
                    state: a.state,
                    triggered_at: a.triggered_at,
                    acknowledged_at: a.acknowledged_at,
                },
            );
        }

        let mut active_trackers = HashMap::new();
        for t in w.active_trackers.unwrap_or_default() {
            active_trackers.insert(t.tracker_definition_id, t.reference_at);
        }

        let mut active_state_spans = HashMap::new();
        for s in w.active_state_spans.unwrap_or_default() {
            let category = ord(&STATE_SPAN_CATEGORY_NAMES, &s.category, "StateSpanCategory")?;
            active_state_spans.insert(
                (category, s.state),
                StateSpanSnapshot {
                    started_at: s.started_at,
                },
            );
        }

        Ok(SensorContext {
            latest_value: opt_dec(w.latest_value.as_ref(), "latest_value")?,
            latest_timestamp: w.latest_timestamp,
            trend_rate: opt_dec(w.trend_rate.as_ref(), "trend_rate")?,
            last_reading_at: w.last_reading_at,
            trend_bucket: w
                .trend_bucket
                .map(|s| ord(&TREND_BUCKET_NAMES, &s, "TrendBucket"))
                .transpose()?,
            iob_units: opt_dec(w.iob_units.as_ref(), "iob_units")?,
            cob_grams: opt_dec(w.cob_grams.as_ref(), "cob_grams")?,
            reservoir_units: opt_dec(w.reservoir_units.as_ref(), "reservoir_units")?,
            reservoir_is_lower_bound: w.reservoir_is_lower_bound.unwrap_or(false),
            last_site_change_at: w.last_site_change_at,
            last_sensor_start_at: w.last_sensor_start_at,
            predictions: w
                .predictions
                .unwrap_or_default()
                .iter()
                .map(|p| {
                    Ok(PredictedPoint {
                        offset_minutes: p.offset_minutes,
                        mgdl: dec(&p.mgdl, "prediction mgdl")?,
                    })
                })
                .collect::<Result<Vec<_>, String>>()?,
            active_alerts,
            last_aps_cycle_at: w.last_aps_cycle_at,
            last_aps_enacted_at: w.last_aps_enacted_at,
            pump_battery_percent: opt_dec(w.pump_battery_percent.as_ref(), "pump_battery_percent")?,
            active_temp_basal: w
                .active_temp_basal
                .map(|tb| {
                    Ok::<_, String>(TempBasalSnapshot {
                        rate: dec(&tb.rate, "temp basal rate")?,
                        scheduled_rate: opt_dec(tb.scheduled_rate.as_ref(), "scheduled_rate")?,
                        started_at: tb.started_at,
                    })
                })
                .transpose()?,
            uploader_battery_percent: opt_dec(
                w.uploader_battery_percent.as_ref(),
                "uploader_battery_percent",
            )?,
            active_override: w.active_override.map(|s| StartedSpan {
                started_at: s.started_at,
            }),
            active_pump_suspension: w.active_pump_suspension.map(|s| StartedSpan {
                started_at: s.started_at,
            }),
            sensitivity_ratio: opt_dec(w.sensitivity_ratio.as_ref(), "sensitivity_ratio")?,
            active_do_not_disturb: w.active_do_not_disturb.map(|s| StartedSpan {
                started_at: s.started_at,
            }),
            has_ever_aps_cycled: w.has_ever_aps_cycled,
            has_ever_pump_snapshot: w.has_ever_pump_snapshot,
            has_ever_uploader_snapshot: w.has_ever_uploader_snapshot,
            has_ever_aps_sensitivity: w.has_ever_aps_sensitivity,
            glucose_bucket: w
                .glucose_bucket
                .map(|s| ord(&GLUCOSE_BUCKET_NAMES, &s, "GlucoseBucket"))
                .transpose()?,
            last_carb_at: w.last_carb_at,
            last_bolus_at: w.last_bolus_at,
            tenant_time_zone_id: w.tenant_time_zone_id,
            active_pump_state: w
                .active_pump_state
                .map(|p| {
                    Ok::<_, String>(PumpStateSnapshot {
                        mode: ord(&PUMP_MODE_NAMES, &p.mode, "PumpModeState")?,
                        started_at: p.started_at,
                    })
                })
                .transpose()?,
            active_state_spans,
            active_trackers,
        })
    }
}
