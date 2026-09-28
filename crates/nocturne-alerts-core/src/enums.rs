//! The enumerations a condition payload or sensor context names by string or
//! ordinal, parsed once when the payload is read.
//!
//! A mirrored enum's declaration order is its ordinal, and an integer payload
//! value is that ordinal, so an enum that drifts from the host's silently
//! rebinds integer payloads to the wrong member. The corpus generator writes
//! every mirrored enum to `tests/Parity/AlertEngineEnums.json`, and
//! `enums_match_manifest` pins each one to it.

use serde::{Serialize, Serializer};

/// How a string payload field is matched against an enum's names.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Fold {
    /// ASCII case-insensitive, as enum names are read.
    AsciiCase,
    /// Unicode-lowercased, then matched exactly (engine-semantics.md §3).
    Lowercase,
}

/// An enum with a fixed, ordered set of wire names.
pub trait WireEnum: Copy + Eq + 'static {
    /// Every member, in ordinal order.
    const ALL: &'static [Self];
    /// The member a missing field reads as: ordinal 0.
    const FIRST: Self;
    /// How [`WireEnum::from_word`] folds the text it reads.
    const FOLD: Fold = Fold::AsciiCase;

    fn name(self) -> &'static str;

    /// The member named `s`, ignoring ASCII case.
    #[must_use]
    fn from_name(s: &str) -> Option<Self> {
        Self::ALL
            .iter()
            .copied()
            .find(|e| e.name().eq_ignore_ascii_case(s))
    }

    #[must_use]
    fn from_ordinal(ordinal: i64) -> Option<Self> {
        usize::try_from(ordinal)
            .ok()
            .and_then(|i| Self::ALL.get(i))
            .copied()
    }

    /// The member a string payload field names, folded per [`WireEnum::FOLD`].
    #[must_use]
    fn from_word(s: &str) -> Option<Self> {
        match Self::FOLD {
            Fold::AsciiCase => Self::from_name(s),
            Fold::Lowercase => Self::from_name(&s.to_lowercase()),
        }
    }
}

macro_rules! wire_enum {
    (
        $(#[$meta:meta])*
        $vis:vis enum $name:ident $(($fold:ident))? {
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
            const FIRST: Self = Self::$first;
            $(const FOLD: $crate::enums::Fold = $crate::enums::Fold::$fold;)?

            fn name(self) -> &'static str {
                match self {
                    Self::$first => $first_wire,
                    $(Self::$variant => $wire),*
                }
            }
        }
    };
}

pub(crate) use wire_enum;

/// An enum-typed payload field: a member, or an integer naming none. Integers
/// are accepted raw, so an undefined one is kept and compares unequal to
/// every member.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EnumValue<E> {
    Known(E),
    Undefined(i32),
}

impl<E: WireEnum> Default for EnumValue<E> {
    fn default() -> Self {
        Self::Known(E::FIRST)
    }
}

impl<E: WireEnum> EnumValue<E> {
    pub(crate) fn from_ordinal(ordinal: i32) -> Self {
        E::from_ordinal(i64::from(ordinal)).map_or(Self::Undefined(ordinal), Self::Known)
    }

    #[must_use]
    pub fn known(self) -> Option<E> {
        match self {
            Self::Known(e) => Some(e),
            Self::Undefined(_) => None,
        }
    }
}

/// A member serialises as its name, an undefined value as its integer.
impl<E: WireEnum> Serialize for EnumValue<E> {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        match self {
            Self::Known(e) => s.serialize_str(e.name()),
            Self::Undefined(i) => s.serialize_i32(*i),
        }
    }
}

/// A string payload field as written, with the member it names.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Spelled<T> {
    pub text: Option<String>,
    pub value: Option<T>,
}

impl<T> Default for Spelled<T> {
    fn default() -> Self {
        Self {
            text: None,
            value: None,
        }
    }
}

impl<T: WireEnum> Spelled<T> {
    pub(crate) fn new(text: Option<String>) -> Self {
        let value = text.as_deref().and_then(T::from_word);
        Self { text, value }
    }
}

/// Serialises as written.
impl<T> Serialize for Spelled<T> {
    fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
        self.text.serialize(s)
    }
}

wire_enum! {
    /// `AlertComparisonOperator`, and the operators a decimal comparison
    /// recognises, matched exactly (engine-semantics.md §3).
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

/// `op` applied to `actual`; false when either is absent.
#[must_use]
pub fn holds<T: PartialOrd>(op: Option<CmpOp>, actual: Option<T>, threshold: T) -> bool {
    op.zip(actual)
        .is_some_and(|(op, actual)| op.apply(actual, threshold))
}

wire_enum! {
    pub enum TempBasalMetric {
        Rate = "rate",
        PercentOfScheduled = "percent_of_scheduled",
    }
}

wire_enum! {
    /// Sunday first, as the host's `DayOfWeek` numbers days.
    pub enum DayOfWeek {
        Sunday = "Sunday",
        Monday = "Monday",
        Tuesday = "Tuesday",
        Wednesday = "Wednesday",
        Thursday = "Thursday",
        Friday = "Friday",
        Saturday = "Saturday",
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
    }
}

wire_enum! {
    /// `threshold.direction`.
    pub enum ThresholdDirection (Lowercase) {
        Above = "above",
        Below = "below",
    }
}

wire_enum! {
    /// `rate_of_change.direction`.
    pub enum RateDirection (Lowercase) {
        Rising = "rising",
        Falling = "falling",
    }
}

wire_enum! {
    /// `composite.operator`.
    pub enum CompositeOp (Lowercase) {
        And = "and",
        Or = "or",
    }
}

wire_enum! {
    /// `alert_state.state`.
    pub enum AlertStateKind (Lowercase) {
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
            ("DayOfWeek", names::<DayOfWeek>()),
            ("GlucoseBucket", names::<GlucoseBucket>()),
            ("PumpModeState", names::<PumpMode>()),
            ("StateSpanCategory", names::<StateSpanCategory>()),
            ("TempBasalMetric", names::<TempBasalMetric>()),
            ("TrendBucket", names::<TrendBucket>()),
            ("AlertConditionType", names::<ConditionKind>()),
            (
                "AlertConditionTypeMembers",
                ConditionKind::ALL.iter().map(|k| k.member()).collect(),
            ),
        ];
        for (enum_name, ours) in tables {
            assert_eq!(listed(enum_name), ours, "{enum_name} drifted from C#");
        }
    }

    /// A property the Rust payload does not declare is stripped from a saved
    /// tree, so each payload must declare exactly what the C# model reads.
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
            let mut ours = crate::model::Payload::default_for(kind).fields().to_vec();
            ours.sort_unstable();
            assert_eq!(ours, theirs, "{} fields drifted from C#", kind.name());
        }
    }

    #[test]
    fn words_fold_per_enum() {
        assert_eq!(
            ThresholdDirection::from_word("BELOW"),
            Some(ThresholdDirection::Below)
        );
        assert_eq!(
            AlertStateKind::from_word("ac\u{212A}nowledged"),
            Some(AlertStateKind::Acknowledged)
        );
        assert_eq!(TrendBucket::from_word("un\u{212A}nown"), None);
        assert_eq!(TrendBucket::from_word("FLAT"), Some(TrendBucket::Flat));
        assert_eq!(CmpOp::from_word(">="), Some(CmpOp::Ge));
        assert_eq!(CmpOp::from_word(" >="), None);
    }
}
