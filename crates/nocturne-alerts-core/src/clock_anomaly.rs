//! Anchors ahead of `now` (engine-semantics.md §5.4): `now` is the host's
//! clock, and an anchor can come from a device whose clock runs ahead of it.

use std::collections::BTreeMap;

use chrono::TimeDelta;
use serde_json::{Map, Value};

use crate::enums::{WireEnum, wire_enum};
use crate::facts::ContextFact;

/// How far ahead of `now` an anchor may be and still read as `now`.
pub const CLOCK_TOLERANCE: TimeDelta = TimeDelta::minutes(5);

/// The time from an anchor to `now`, given `now - anchor`: as it is from the
/// anchor on, zero for an anchor at most [`CLOCK_TOLERANCE`] ahead, and the
/// distance ahead as the error beyond that, where the elapsed time is not
/// known.
pub(crate) fn since_anchor(now_minus_anchor: TimeDelta) -> Result<TimeDelta, TimeDelta> {
    if now_minus_anchor >= TimeDelta::zero() {
        Ok(now_minus_anchor)
    } else if now_minus_anchor >= -CLOCK_TOLERANCE {
        Ok(TimeDelta::zero())
    } else {
        Err(now_minus_anchor.abs())
    }
}

wire_enum! {
    /// An instant an elapsed time is measured from, named as the context
    /// field (engine-semantics.md §4) or evaluation state that holds it.
    #[derive(PartialOrd, Ord)]
    pub enum ClockAnchor {
        LastReadingAt = "last_reading_at",
        LatestTimestamp = "latest_timestamp",
        EnabledAt = "enabled_at",
        LastApsCycleAt = "last_aps_cycle_at",
        LastApsEnactedAt = "last_aps_enacted_at",
        LastSiteChangeAt = "last_site_change_at",
        LastSensorStartAt = "last_sensor_start_at",
        ActiveTrackers = "active_trackers",
        LastCarbAt = "last_carb_at",
        LastBolusAt = "last_bolus_at",
        ActiveAlerts = "active_alerts",
        ActivePumpSuspension = "active_pump_suspension",
        ActiveOverride = "active_override",
        ActiveDoNotDisturb = "active_do_not_disturb",
        ActivePumpModes = "active_pump_modes",
        ActiveStateSpans = "active_state_spans",
        SensorState = "sensor_state",
        ApsDetermination = "aps_determination",
        Sustained = "sustained",
        HysteresisStartedAt = "hysteresis_started_at",
        HeldSince = "held_since",
    }
}

impl ClockAnchor {
    /// The context fact that holds the anchor (engine-semantics.md §5.2);
    /// `None` for the rule's and the engine's own instants.
    #[must_use]
    pub fn fact(self) -> Option<ContextFact> {
        use ContextFact as F;
        match self {
            ClockAnchor::LastReadingAt | ClockAnchor::LatestTimestamp | ClockAnchor::EnabledAt => {
                Some(F::Glucose)
            }
            ClockAnchor::LastApsCycleAt | ClockAnchor::LastApsEnactedAt => Some(F::Aps),
            ClockAnchor::LastSiteChangeAt | ClockAnchor::LastSensorStartAt => Some(F::DeviceAges),
            ClockAnchor::ActiveTrackers => Some(F::Trackers),
            ClockAnchor::LastCarbAt => Some(F::Carbs),
            ClockAnchor::LastBolusAt => Some(F::Insulin),
            ClockAnchor::ActiveAlerts => Some(F::ActiveAlerts),
            ClockAnchor::ActivePumpSuspension
            | ClockAnchor::ActiveOverride
            | ClockAnchor::ActivePumpModes
            | ClockAnchor::ActiveStateSpans => Some(F::StateSpans),
            ClockAnchor::ActiveDoNotDisturb => Some(F::DoNotDisturb),
            ClockAnchor::SensorState => Some(F::SensorState),
            ClockAnchor::ApsDetermination => Some(F::ApsDetermination),
            ClockAnchor::Sustained | ClockAnchor::HysteresisStartedAt | ClockAnchor::HeldSince => {
                None
            }
        }
    }
}

/// Every anchor an evaluation read beyond [`CLOCK_TOLERANCE`] ahead of
/// `now`, with the furthest distance ahead it was read at.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ClockAnomalies(BTreeMap<ClockAnchor, TimeDelta>);

impl ClockAnomalies {
    pub(crate) fn note(&mut self, ahead: Option<(ClockAnchor, TimeDelta)>) {
        if let Some((anchor, by)) = ahead {
            let furthest = self.0.entry(anchor).or_insert(by);
            *furthest = (*furthest).max(by);
        }
    }

    pub(crate) fn merge(&mut self, other: ClockAnomalies) {
        for (anchor, by) in other.0 {
            self.note(Some((anchor, by)));
        }
    }

    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }

    /// In [`ClockAnchor`] order.
    pub fn iter(&self) -> impl Iterator<Item = (ClockAnchor, TimeDelta)> + '_ {
        self.0.iter().map(|(&anchor, &by)| (anchor, by))
    }

    /// `[{"fact": <anchor>, "ahead_ms": <whole milliseconds>}]`.
    #[must_use]
    pub fn to_json(&self) -> Value {
        self.iter()
            .map(|(anchor, by)| {
                let mut o = Map::new();
                o.insert("fact".into(), anchor.name().into());
                o.insert("ahead_ms".into(), by.num_milliseconds().into());
                Value::Object(o)
            })
            .collect()
    }

    /// Inserts `clock_anomaly` into `o` when there is any.
    pub fn write(&self, o: &mut Map<String, Value>) {
        if !self.is_empty() {
            o.insert("clock_anomaly".into(), self.to_json());
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn exactly_the_tolerance_ahead_reads_as_now() {
        assert_eq!(since_anchor(-CLOCK_TOLERANCE), Ok(TimeDelta::zero()));
        let beyond = CLOCK_TOLERANCE + TimeDelta::nanoseconds(1);
        assert_eq!(since_anchor(-beyond), Err(beyond));
        assert_eq!(
            since_anchor(TimeDelta::minutes(7)),
            Ok(TimeDelta::minutes(7))
        );
    }
}
