//! The enumerations a condition payload or sensor context names by string,
//! parsed once when the payload is read. A value is exactly one member's wire
//! name (engine-semantics.md §1.1).
//!
//! Every mirrored enum is listed in `tests/Parity/AlertEngineEnums.json`,
//! which the backend's `EnumManifestTests` keeps equal to the C# enums, and
//! `enums_match_manifest` pins each one to it.

/// An enum with a fixed set of wire names.
pub trait WireEnum: Copy + Eq + 'static {
    /// Every member, in declaration order.
    const ALL: &'static [Self];

    fn name(self) -> &'static str;

    /// The member whose wire name is exactly `s`.
    #[must_use]
    fn from_name(s: &str) -> Option<Self> {
        Self::ALL.iter().copied().find(|e| e.name() == s)
    }
}

macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        $vis:vis enum $name:ident {
            $first:ident = $first_wire:literal
            $(, $variant:ident = $wire:literal)* $(,)?
        }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
        $vis enum $name {
            $first,
            $($variant),*
        }

        impl $crate::enums::WireEnum for $name {
            const ALL: &'static [Self] = &[Self::$first, $(Self::$variant),*];

            fn name(self) -> &'static str {
                match self {
                    Self::$first => $first_wire,
                    $(Self::$variant => $wire),*
                }
            }
        }

        /// Serialises as its wire name.
        impl serde::Serialize for $name {
            fn serialize<S: serde::Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
                s.serialize_str($crate::enums::WireEnum::name(*self))
            }
        }
    };
}

pub(crate) use wire_enum;

wire_enum! {
    /// `AlertComparisonOperator`, and the operators a decimal comparison
    /// recognises (engine-semantics.md §3).
    pub enum CmpOp {
        Gt = ">",
        Ge = ">=",
        Lt = "<",
        Le = "<=",
        Eq = "==",
    }
}

impl CmpOp {
    #[must_use]
    pub fn apply<T: PartialOrd>(self, actual: T, threshold: T) -> bool {
        match self {
            Self::Gt => actual > threshold,
            Self::Ge => actual >= threshold,
            Self::Lt => actual < threshold,
            Self::Le => actual <= threshold,
            Self::Eq => actual == threshold,
        }
    }
}

/// `op` applied to `actual`; false when it is absent.
#[must_use]
pub fn holds<T: PartialOrd>(op: CmpOp, actual: Option<T>, threshold: T) -> bool {
    actual.is_some_and(|actual| op.apply(actual, threshold))
}

wire_enum! {
    pub enum TempBasalMetric {
        Rate = "rate",
        PercentOfScheduled = "percent_of_scheduled",
    }
}

wire_enum! {
    /// `AlertWeekday`.
    pub enum DayOfWeek {
        Sunday = "sunday",
        Monday = "monday",
        Tuesday = "tuesday",
        Wednesday = "wednesday",
        Thursday = "thursday",
        Friday = "friday",
        Saturday = "saturday",
    }
}

wire_enum! {
    pub enum GlucoseBucket {
        VeryLow = "very_low",
        Low = "low",
        TightRange = "tight_range",
        InRange = "in_range",
        High = "high",
        VeryHigh = "very_high",
    }
}

wire_enum! {
    pub enum TrendBucket {
        Unknown = "unknown",
        RisingFast = "rising_fast",
        Rising = "rising",
        Flat = "flat",
        Falling = "falling",
        FallingFast = "falling_fast",
    }
}

wire_enum! {
    /// `PumpModeState`.
    pub enum PumpMode {
        Automatic = "Automatic",
        Limited = "Limited",
        Manual = "Manual",
        Boost = "Boost",
        EaseOff = "EaseOff",
        Sleep = "Sleep",
        Exercise = "Exercise",
        Liberty = "Liberty",
        Suspended = "Suspended",
        Off = "Off",
    }
}

wire_enum! {
    pub enum StateSpanCategory {
        PumpMode = "PumpMode",
        PumpConnectivity = "PumpConnectivity",
        Override = "Override",
        Profile = "Profile",
        Exercise = "Exercise",
        Illness = "Illness",
        Travel = "Travel",
        DataExclusion = "DataExclusion",
        TemporaryTarget = "TemporaryTarget",
        Motion = "Motion",
    }
}

wire_enum! {
    /// `threshold.direction`.
    pub enum ThresholdDirection {
        Above = "above",
        Below = "below",
    }
}

wire_enum! {
    /// `rate_of_change.direction`.
    pub enum RateDirection {
        Rising = "rising",
        Falling = "falling",
    }
}

wire_enum! {
    /// `composite.operator`.
    pub enum CompositeOp {
        And = "and",
        Or = "or",
    }
}

wire_enum! {
    /// `alert_state.state`.
    pub enum AlertStateKind {
        Firing = "firing",
        Unacknowledged = "unacknowledged",
        Acknowledged = "acknowledged",
    }
}

#[cfg(test)]
mod tests {
    #![allow(clippy::expect_used, clippy::panic)]

    use serde_json::Value;

    use super::*;
    use crate::model::ConditionKind;

    fn names<E: WireEnum>() -> Vec<&'static str> {
        E::ALL.iter().map(|e| e.name()).collect()
    }

    #[test]
    fn enums_match_manifest() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../tests/Parity/AlertEngineEnums.json"
        );
        let text = std::fs::read_to_string(path).expect("read enum manifest");
        let manifest: Value = serde_json::from_str(&text).expect("parse enum manifest");
        let listed = |enum_name: &str| -> Vec<&str> {
            manifest[enum_name]
                .as_array()
                .unwrap_or_else(|| panic!("{enum_name} missing from manifest"))
                .iter()
                .map(|v| v.as_str().expect("enum name is a string"))
                .collect()
        };
        let tables = [
            ("AlertComparisonOperator", names::<CmpOp>()),
            ("AlertWeekday", names::<DayOfWeek>()),
            ("GlucoseBucket", names::<GlucoseBucket>()),
            ("PumpModeState", names::<PumpMode>()),
            ("StateSpanCategory", names::<StateSpanCategory>()),
            ("TempBasalMetric", names::<TempBasalMetric>()),
            ("TrendBucket", names::<TrendBucket>()),
            ("AlertConditionType", names::<ConditionKind>()),
        ];
        for (enum_name, ours) in tables {
            assert_eq!(listed(enum_name), ours, "{enum_name} drifted from C#");
        }
    }

    /// A property the Rust payload does not declare keeps a tree from being
    /// evaluated, so each payload must declare exactly what the C# model reads.
    #[test]
    fn payload_fields_match_the_csharp_models() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../tests/Parity/AlertEngineEnums.json"
        );
        let text = std::fs::read_to_string(path).expect("read enum manifest");
        let manifest: Value = serde_json::from_str(&text).expect("parse enum manifest");
        let listed = manifest["PayloadFields"]
            .as_object()
            .expect("PayloadFields missing from manifest");
        assert_eq!(listed.len(), ConditionKind::ALL.len(), "one entry per kind");
        for &kind in ConditionKind::ALL {
            let theirs: Vec<&str> = listed[kind.name()]
                .as_array()
                .unwrap_or_else(|| panic!("{} missing from PayloadFields", kind.name()))
                .iter()
                .map(|v| v.as_str().expect("field name is a string"))
                .collect();
            let mut ours = crate::model::Payload::fields(kind).to_vec();
            ours.sort_unstable();
            assert_eq!(ours, theirs, "{} fields drifted from C#", kind.name());
        }
    }

    #[test]
    fn a_name_matches_only_exactly() {
        assert_eq!(
            ThresholdDirection::from_name("below"),
            Some(ThresholdDirection::Below)
        );
        assert_eq!(ThresholdDirection::from_name("BELOW"), None);
        assert_eq!(AlertStateKind::from_name("ac\u{212A}nowledged"), None);
        assert_eq!(TrendBucket::from_name("Flat"), None);
        assert_eq!(PumpMode::from_name("suspended"), None);
        assert_eq!(DayOfWeek::from_name("Monday"), None);
        assert_eq!(DayOfWeek::from_name("1"), None);
        assert_eq!(CmpOp::from_name(">="), Some(CmpOp::Ge));
        assert_eq!(CmpOp::from_name(" >="), None);
    }
}
