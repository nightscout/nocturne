//! Condition-tree model: JSON-compatible with the stored `condition_params` /
//! `auto_resolve_params` wire format (engine-semantics.md §1).
//!
//! A tree reads into this model only when it can be evaluated as written
//! (§1.4): exact snake_case property names (§1.1), every required operand
//! present and in range, every enum value exactly a member's wire name, every
//! container with its children. Reading reports every problem it finds, in
//! pre-order, and yields no tree when there is any.

use std::cell::RefCell;

use chrono::NaiveTime;
use chrono_tz::Tz;
use rust_decimal::Decimal;
use serde::{Serialize, Serializer};
use serde_json::{Map, Value};
use uuid::Uuid;

use crate::enums::{
    AlertStateKind, CmpOp, CompositeOp, DayOfWeek, GlucoseBucket, PumpMode, RateDirection,
    StateSpanCategory, TempBasalMetric, ThresholdDirection, TrendBucket, WireEnum,
};
use crate::paths::child_path;

/// Why a condition tree cannot be evaluated (engine-semantics.md §1.4).
/// [`Reason::code`] is the stable wire code; no reason carries a value read
/// from the payload.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum Reason {
    /// A node or root payload is not a JSON object.
    NotAnObject,
    /// The named field has the wrong JSON kind or an unrepresentable value, or
    /// a `timezone` is not a zone id.
    InvalidField(&'static str),
    /// Nested deeper than the reader's `MaxDepth`.
    TooDeep,
    /// A node with no `type`.
    TypeMissing,
    /// A `null` slot in a composite's `conditions`.
    ConditionMissing,
    /// A node with no payload property for its `type`, or a rule body whose
    /// `condition_params` is JSON `null`.
    PayloadMissing,
    /// A `type` that is not exactly a kind's wire name.
    UnknownKind,
    /// A duration that is zero or negative where that leaves its node never
    /// true: `sustained` `minutes`, `signal_loss` `timeout_minutes`,
    /// `predicted` `within_minutes`.
    MinutesNotPositive(&'static str),
    /// A negative elapsed-time bound, which every operator compares the same
    /// way against an elapsed time that is never negative.
    MinutesNegative(&'static str),
    /// A property no node of its kind, or no payload of its kind, has.
    UnknownField,
    /// A required operand, or a `not`/`sustained` `child`, is absent.
    FieldMissing(&'static str),
    /// An enum operand naming no member.
    UnknownValue(&'static str),
    /// A `time_of_day` bound that is not `HH:mm`.
    InvalidTime(&'static str),
    /// A `time_of_day` whose `from` equals its `to`.
    EmptyWindow,
    /// A composite's `conditions`, or a `glucose_bucket` or `day_of_week`
    /// list, that is absent or empty.
    ListEmpty(&'static str),
    /// A `state_span_active` on the pump-mode category, which only
    /// `pump_state` reads.
    PumpModeCategory,
}

impl Reason {
    #[must_use]
    pub fn code(self) -> &'static str {
        match self {
            Reason::NotAnObject => "not_an_object",
            Reason::InvalidField(_) => "invalid_field",
            Reason::TooDeep => "too_deep",
            Reason::TypeMissing => "type_missing",
            Reason::ConditionMissing => "condition_missing",
            Reason::PayloadMissing => "payload_missing",
            Reason::UnknownKind => "unknown_kind",
            Reason::MinutesNotPositive(_) => "minutes_not_positive",
            Reason::MinutesNegative(_) => "minutes_negative",
            Reason::UnknownField => "unknown_field",
            Reason::FieldMissing(_) => "field_missing",
            Reason::UnknownValue(_) => "unknown_value",
            Reason::InvalidTime(_) => "invalid_time",
            Reason::EmptyWindow => "empty_window",
            Reason::ListEmpty(_) => "list_empty",
            Reason::PumpModeCategory => "pump_mode_category",
        }
    }

    /// The payload field the problem is on, when it is on one.
    #[must_use]
    pub fn field(self) -> Option<&'static str> {
        match self {
            Reason::InvalidField(f)
            | Reason::FieldMissing(f)
            | Reason::UnknownValue(f)
            | Reason::InvalidTime(f)
            | Reason::ListEmpty(f)
            | Reason::MinutesNotPositive(f)
            | Reason::MinutesNegative(f) => Some(f),
            Reason::PumpModeCategory => Some("category"),
            Reason::TypeMissing | Reason::UnknownKind => Some("type"),
            Reason::NotAnObject
            | Reason::TooDeep
            | Reason::ConditionMissing
            | Reason::PayloadMissing
            | Reason::UnknownField
            | Reason::EmptyWindow => None,
        }
    }
}

/// A problem with a condition tree, and the condition path
/// (engine-semantics.md §2.3) of the node it is on.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ParseError {
    pub path: String,
    pub reason: Reason,
}

impl ParseError {
    pub fn new(path: impl Into<String>, reason: Reason) -> Self {
        Self {
            path: path.into(),
            reason,
        }
    }
}

impl std::fmt::Display for ParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            f,
            "{} at '{}'",
            self.reason.code(),
            self.path.escape_default()
        )?;
        if let Some(field) = self.reason.field() {
            write!(f, " (field '{field}')")?;
        }
        Ok(())
    }
}

impl std::error::Error for ParseError {}

/// Every problem that keeps a tree from being evaluated, in pre-order; never
/// empty.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Unevaluable(pub Vec<ParseError>);

impl std::fmt::Display for Unevaluable {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        for (i, issue) in self.0.iter().enumerate() {
            if i > 0 {
                f.write_str("; ")?;
            }
            write!(f, "{issue}")?;
        }
        Ok(())
    }
}

impl std::error::Error for Unevaluable {}

pub type ParseResult<T> = Result<T, Unevaluable>;

/// Declares [`ConditionKind`] (in the host enum's declaration order) and
/// [`Payload`], one variant per kind.
macro_rules! condition_kinds {
    ($($kind:ident = $wire:literal => $payload:ident),+ $(,)?) => {
        crate::enums::wire_enum! {
            /// The condition kinds. [`WireEnum::name`] is the wire name.
            pub enum ConditionKind { $($kind = $wire),+ }
        }

        /// A parsed kind-specific payload. Serialises to its authored operands.
        #[derive(Debug, Clone, Serialize)]
        #[serde(untagged)]
        pub enum Payload {
            $($kind($payload)),+
        }

        impl Payload {
            #[must_use]
            pub fn kind(&self) -> ConditionKind {
                match self {
                    $(Self::$kind(_) => ConditionKind::$kind),+
                }
            }

            /// The payload property names `kind` reads.
            #[must_use]
            pub fn fields(kind: ConditionKind) -> &'static [&'static str] {
                match kind {
                    $(ConditionKind::$kind => $payload::FIELDS),+
                }
            }

            fn read(kind: ConditionKind, r: &Reader<'_>) -> Option<Self> {
                match kind {
                    $(ConditionKind::$kind => $payload::read(r).map(Self::$kind)),+
                }
            }
        }
    };
}

condition_kinds! {
    Threshold = "threshold" => ThresholdPayload,
    RateOfChange = "rate_of_change" => RateOfChangePayload,
    SignalLoss = "signal_loss" => SignalLossPayload,
    Composite = "composite" => CompositePayload,
    Not = "not" => NotPayload,
    Sustained = "sustained" => SustainedPayload,
    Staleness = "staleness" => StalenessPayload,
    Predicted = "predicted" => PredictedPayload,
    Trend = "trend" => TrendPayload,
    TimeOfDay = "time_of_day" => TimeOfDayPayload,
    Iob = "iob" => ComparePayload,
    Cob = "cob" => ComparePayload,
    Reservoir = "reservoir" => ComparePayload,
    SiteAge = "site_age" => ComparePayload,
    SensorAge = "sensor_age" => ComparePayload,
    AlertState = "alert_state" => AlertStatePayload,
    LoopStale = "loop_stale" => MinutesComparePayload,
    LoopEnactionStale = "loop_enaction_stale" => MinutesComparePayload,
    PumpSuspended = "pump_suspended" => ActiveForPayload,
    PumpBattery = "pump_battery" => ComparePayload,
    TempBasal = "temp_basal" => TempBasalPayload,
    UploaderBattery = "uploader_battery" => ComparePayload,
    OverrideActive = "override_active" => ActiveForPayload,
    SensitivityRatio = "sensitivity_ratio" => ComparePayload,
    DoNotDisturb = "do_not_disturb" => ActiveForPayload,
    GlucoseBucket = "glucose_bucket" => GlucoseBucketPayload,
    TimeSinceLastCarb = "time_since_last_carb" => TimeSincePayload,
    TimeSinceLastBolus = "time_since_last_bolus" => TimeSincePayload,
    DayOfWeek = "day_of_week" => DayOfWeekPayload,
    PumpState = "pump_state" => PumpStatePayload,
    StateSpanActive = "state_span_active" => StateSpanPayload,
    SleepSessionActive = "sleep_session_active" => SleepSessionPayload,
    TrackerAge = "tracker_age" => TrackerAgePayload,
}

impl ConditionKind {
    /// The kind whose wire name is exactly `type_str` (engine-semantics.md
    /// §1.2).
    #[must_use]
    pub fn from_wire(type_str: &str) -> Option<Self> {
        Self::ALL.iter().copied().find(|k| k.name() == type_str)
    }
}

/// Decimal from a JSON number literal (engine-semantics.md §1.3), with no
/// binary-float round trip. Digits accumulate into the 96-bit mantissa until
/// it would overflow or the scale reaches 28; the next digit rounds
/// half-to-even, where a tie is a `5` followed only by zeros within the first
/// 29 significant digits. A magnitude below the smallest scale-28 step
/// becomes zero rather than an error; `None` when the value exceeds the
/// decimal range or the literal is not a JSON number.
pub(crate) fn parse_decimal_literal(s: &str) -> Option<Decimal> {
    const MAX_SCALE: i64 = 28;
    const MAX_MANTISSA: u128 = (1 << 96) - 1;
    // Digits past the 29th reach the rounding step only as a "non-zero tail"
    // flag, so a 5 in the 30th place always rounds up.
    const DIGIT_BUFFER: usize = 29;

    let (negative, unsigned) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s),
    };
    let (mantissa_text, exponent) = match unsigned.split_once(['e', 'E']) {
        Some((mantissa, exponent)) => (mantissa, parse_saturating_exponent(exponent)?),
        None => (unsigned, 0),
    };
    let (int_part, frac_part) = mantissa_text.split_once('.').unwrap_or((mantissa_text, ""));
    let all_digits = int_part.bytes().chain(frac_part.bytes());
    if int_part.is_empty() || !all_digits.clone().all(|b| b.is_ascii_digit()) {
        return None;
    }

    let leading_zeros = all_digits.clone().take_while(|&b| b == b'0').count();
    let digits: Vec<u8> = all_digits
        .skip(leading_zeros)
        .map(|b| b.wrapping_sub(b'0'))
        .collect();
    // `value = 0.d1d2d3… × 10^e`, saturated so an absurd exponent cannot wrap.
    let mut e = len_i64(int_part.len())
        .saturating_sub(len_i64(leading_zeros))
        .saturating_add(exponent);

    if digits.is_empty() {
        let scale = len_i64(frac_part.len()).saturating_sub(exponent);
        return Some(Decimal::from_i128_with_scale(0, scale_u32(scale)));
    }
    if e > MAX_SCALE + 1 {
        return None;
    }

    let mut mantissa: u128 = 0;
    let mut next = 0;
    while e > 0 || (next < digits.len() && e > -MAX_SCALE) {
        let digit = digits.get(next).copied().unwrap_or(0);
        let Some(widened) = mantissa
            .checked_mul(10)
            .and_then(|m| m.checked_add(u128::from(digit)))
            .filter(|&m| m <= MAX_MANTISSA)
        else {
            break;
        };
        mantissa = widened;
        if next < digits.len() {
            next = next.saturating_add(1);
        }
        e = e.saturating_sub(1);
    }

    if let Some((&digit, tail)) = digits.get(next..).and_then(<[u8]>::split_first) {
        let tie_to_even = next < DIGIT_BUFFER
            && digit == 5
            && mantissa.is_multiple_of(2)
            && tail.iter().all(|&d| d == 0);
        if digit >= 5 && !tie_to_even {
            mantissa = mantissa.saturating_add(1);
            if mantissa > MAX_MANTISSA {
                mantissa = MAX_MANTISSA / 10 + 1;
                e = e.saturating_add(1);
            }
        }
    }

    if e > 0 {
        return None;
    }
    let mut d = if e <= -(MAX_SCALE + 1) {
        Decimal::from_i128_with_scale(0, scale_u32(MAX_SCALE))
    } else {
        let scale = scale_u32(e.saturating_neg());
        Decimal::try_from_i128_with_scale(i128::try_from(mantissa).ok()?, scale).ok()?
    };
    d.set_sign_negative(negative && !d.is_zero());
    Some(d)
}

fn len_i64(len: usize) -> i64 {
    i64::try_from(len).unwrap_or(i64::MAX)
}

/// A decimal scale clamped to `0..=28`.
fn scale_u32(scale: i64) -> u32 {
    u32::try_from(scale.clamp(0, 28)).unwrap_or(28)
}

/// A JSON exponent (`[+-]?digits`), saturated to ±`i64::MAX / 2` so later
/// scale arithmetic stays in range.
fn parse_saturating_exponent(s: &str) -> Option<i64> {
    let (negative, digits) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s.strip_prefix('+').unwrap_or(s)),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let limit = i64::MAX / 2;
    let magnitude = digits.bytes().fold(0i64, |acc, b| {
        acc.saturating_mul(10)
            .saturating_add(i64::from(b.wrapping_sub(b'0')))
            .min(limit)
    });
    Some(if negative {
        magnitude.saturating_neg()
    } else {
        magnitude
    })
}

pub(crate) fn decimal_from_number(n: &serde_json::Number) -> Option<Decimal> {
    parse_decimal_literal(n.as_str())
}

/// Serialises a decimal operand as the exact JSON number it prints as.
fn exact_decimal<S: Serializer>(d: &Decimal, s: S) -> Result<S::Ok, S::Error> {
    let n: serde_json::Number = d.to_string().parse().map_err(serde::ser::Error::custom)?;
    n.serialize(s)
}

/// Every problem found while reading one tree, in pre-order.
#[derive(Default)]
struct Issues(RefCell<Vec<ParseError>>);

impl Issues {
    /// Records `reason` at `path`. Always `false`, so a check can end in it.
    fn report(&self, path: &str, reason: Reason) -> bool {
        self.0.borrow_mut().push(ParseError::new(path, reason));
        false
    }
}

/// The object a payload's fields are read from. `level` is the typed nesting
/// level of the values inside it; `path` is the owning node's condition path.
struct Reader<'a> {
    obj: &'a Map<String, Value>,
    level: usize,
    path: &'a str,
    issues: &'a Issues,
}

impl Reader<'_> {
    /// The property `name`; `null` reads as absent.
    fn get(&self, name: &str) -> Option<&Value> {
        self.obj.get(name).filter(|v| !v.is_null())
    }

    fn report(&self, reason: Reason) -> bool {
        self.issues.report(self.path, reason)
    }

    fn ok<T>(&self, read: Result<T, Reason>) -> Option<T> {
        read.map_err(|reason| self.report(reason)).ok()
    }

    /// One [`Reason::UnknownField`] when any property that is not `null` is
    /// not `known`.
    fn report_unknown(&self, known: impl Fn(&str) -> bool) {
        if self.obj.iter().any(|(k, v)| !v.is_null() && !known(k)) {
            self.report(Reason::UnknownField);
        }
    }

    /// An operand that must be present.
    fn required<T>(&self, name: &'static str, parse: OperandParser<T>) -> Option<T> {
        match self.get(name) {
            None => {
                self.report(Reason::FieldMissing(name));
                None
            }
            Some(v) => self.ok(parse(v, name)),
        }
    }

    /// An operand that may be absent.
    fn optional<T>(&self, name: &'static str, parse: OperandParser<T>) -> Option<Option<T>> {
        self.get(name)
            .map_or(Some(None), |v| self.ok(parse(v, name)).map(Some))
    }

    /// The JSON array `name`, which must hold at least one item.
    fn list(&self, name: &'static str) -> Option<&Vec<Value>> {
        let reason = match self.get(name) {
            Some(Value::Array(items)) if !within_depth(self.level) => Reason::TooDeep,
            Some(Value::Array(items)) if !items.is_empty() => return Some(items),
            Some(Value::Array(_)) | None => Reason::ListEmpty(name),
            Some(_) => Reason::InvalidField(name),
        };
        self.report(reason);
        None
    }
}

type OperandParser<T> = fn(&Value, &'static str) -> Result<T, Reason>;

/// A payload operand read from one JSON value (engine-semantics.md §1.1).
trait Operand: Sized {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason>;
}

/// A payload field: an [`Operand`], required or optional, a non-empty list,
/// or a child node.
trait Field: Sized {
    /// `None` once a problem with the field is reported.
    fn read(r: &Reader<'_>, name: &'static str) -> Option<Self>;
}

macro_rules! operand_fields {
    ($($t:ty),* $(,)?) => {$(
        impl Field for $t {
            fn read(r: &Reader<'_>, name: &'static str) -> Option<Self> {
                r.required(name, <$t as Operand>::parse)
            }
        }

        impl Field for Option<$t> {
            fn read(r: &Reader<'_>, name: &'static str) -> Option<Self> {
                r.optional(name, <$t as Operand>::parse)
            }
        }
    )*};
}

fn i32_from(n: &serde_json::Number) -> Option<i32> {
    n.as_i64().and_then(|v| i32::try_from(v).ok())
}

impl Operand for Decimal {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_number()
            .and_then(decimal_from_number)
            .ok_or(Reason::InvalidField(name))
    }
}

impl Operand for i32 {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_number()
            .and_then(i32_from)
            .ok_or(Reason::InvalidField(name))
    }
}

impl Operand for bool {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_bool().ok_or(Reason::InvalidField(name))
    }
}

impl Operand for String {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_str()
            .map(str::to_owned)
            .ok_or(Reason::InvalidField(name))
    }
}

/// Only the 36-character hyphenated form reads; braced, simple and URN
/// spellings do not.
impl Operand for Uuid {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_str()
            .filter(|s| s.len() == 36)
            .and_then(|s| Uuid::try_parse(s).ok())
            .ok_or(Reason::InvalidField(name))
    }
}

/// Exactly an IANA tzdb name (engine-semantics.md §4).
impl Operand for Tz {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        v.as_str()
            .and_then(|id| id.parse().ok())
            .ok_or(Reason::InvalidField(name))
    }
}

/// `HH:mm` exactly: two digits each, 00-23 and 00-59.
impl Operand for NaiveTime {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        let text = v.as_str().ok_or(Reason::InvalidField(name))?;
        let two_digits = |t: &str| {
            (t.len() == 2 && t.bytes().all(|b| b.is_ascii_digit()))
                .then(|| t.parse().ok())
                .flatten()
        };
        text.split_once(':')
            .and_then(|(hh, mm)| NaiveTime::from_hms_opt(two_digits(hh)?, two_digits(mm)?, 0))
            .ok_or(Reason::InvalidTime(name))
    }
}

/// A duration that leaves its node never true unless it is more than zero.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct PositiveMinutes(i32);

impl PositiveMinutes {
    #[must_use]
    pub fn get(self) -> i32 {
        self.0
    }
}

impl Operand for PositiveMinutes {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        match i32::parse(v, name)? {
            m if m > 0 => Ok(Self(m)),
            _ => Err(Reason::MinutesNotPositive(name)),
        }
    }
}

/// A bound on an elapsed time, which is never negative, so a negative bound
/// compares the same way on every tick.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(transparent)]
pub struct NonNegativeMinutes(i32);

impl NonNegativeMinutes {
    #[must_use]
    pub fn get(self) -> i32 {
        self.0
    }
}

impl Operand for NonNegativeMinutes {
    fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
        match i32::parse(v, name)? {
            m if m >= 0 => Ok(Self(m)),
            _ => Err(Reason::MinutesNegative(name)),
        }
    }
}

/// A string that is exactly a member's wire name.
fn word<E: WireEnum>(v: &Value, name: &'static str) -> Result<E, Reason> {
    let text = v.as_str().ok_or(Reason::InvalidField(name))?;
    E::from_name(text).ok_or(Reason::UnknownValue(name))
}

macro_rules! enum_operands {
    ($($e:ty),* $(,)?) => {$(
        impl Operand for $e {
            fn parse(v: &Value, name: &'static str) -> Result<Self, Reason> {
                word(v, name)
            }
        }
    )*};
}

enum_operands!(
    ThresholdDirection,
    RateDirection,
    CompositeOp,
    AlertStateKind,
    CmpOp,
    TrendBucket,
    TempBasalMetric,
    PumpMode,
    StateSpanCategory,
);

operand_fields!(
    Decimal,
    i32,
    bool,
    String,
    Uuid,
    Tz,
    NaiveTime,
    PositiveMinutes,
    NonNegativeMinutes,
    ThresholdDirection,
    RateDirection,
    CompositeOp,
    AlertStateKind,
    CmpOp,
    TrendBucket,
    TempBasalMetric,
    PumpMode,
    StateSpanCategory,
);

macro_rules! enum_lists {
    ($($e:ty),* $(,)?) => {$(
        impl Field for Vec<$e> {
            fn read(r: &Reader<'_>, name: &'static str) -> Option<Self> {
                let items = r.list(name)?;
                r.ok(items.iter().map(|v| word(v, name)).collect())
            }
        }
    )*};
}

enum_lists!(GlucoseBucket, DayOfWeek);

impl Field for Box<Node> {
    fn read(r: &Reader<'_>, name: &'static str) -> Option<Self> {
        let Some(v) = r.get(name) else {
            r.report(Reason::FieldMissing(name));
            return None;
        };
        let path = child_path(r.path, 0, peek_type(v));
        read_node(v, r.level, &path, r.issues).map(Box::new)
    }
}

/// A `null` slot is [`Reason::ConditionMissing`].
impl Field for Vec<Node> {
    fn read(r: &Reader<'_>, name: &'static str) -> Option<Self> {
        let items = r.list(name)?;
        let level = r.level.saturating_add(1);
        let children: Vec<Option<Node>> = items
            .iter()
            .enumerate()
            .map(|(i, v)| {
                let path = child_path(r.path, i, peek_type(v));
                if v.is_null() {
                    r.issues.report(&path, Reason::ConditionMissing);
                    None
                } else {
                    read_node(v, level, &path, r.issues)
                }
            })
            .collect();
        children.into_iter().collect()
    }
}

/// Declares a payload record whose fields read, in declaration order, from
/// the JSON properties of the same names. `check` runs on a payload whose
/// fields all read, and reports what is wrong with them together.
macro_rules! payload {
    (
        $(#[$meta:meta])* $name:ident {
            $($(#[$field_meta:meta])* $field:ident: $ty:ty),* $(,)?
        }
        $(check |$p:ident, $r:ident| $check:expr)?
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Serialize)]
        pub struct $name {
            $($(#[$field_meta])* pub $field: $ty),*
        }

        impl $name {
            const FIELDS: &'static [&'static str] = &[$(stringify!($field)),*];

            fn read(r: &Reader<'_>) -> Option<Self> {
                r.report_unknown(|k| Self::FIELDS.contains(&k));
                $(let $field = <$ty as Field>::read(r, stringify!($field));)*
                let payload = Self { $($field: $field?),* };
                $({
                    let ($p, $r) = (&payload, r);
                    if !$check {
                        return None;
                    }
                })?
                Some(payload)
            }
        }
    };
}

payload!(ThresholdPayload {
    direction: ThresholdDirection,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
});

payload!(RateOfChangePayload {
    direction: RateDirection,
    #[serde(serialize_with = "exact_decimal")]
    rate: Decimal,
});

payload!(SignalLossPayload {
    timeout_minutes: PositiveMinutes
});

payload!(CompositePayload {
    operator: CompositeOp,
    #[serde(skip)]
    conditions: Vec<Node>,
});

payload!(NotPayload {
    #[serde(skip)]
    child: Box<Node>,
});

payload!(SustainedPayload {
    minutes: PositiveMinutes,
    #[serde(skip)]
    child: Box<Node>,
});

payload!(StalenessPayload {
    operator: CmpOp,
    value: NonNegativeMinutes,
});

payload!(PredictedPayload {
    operator: CmpOp,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
    within_minutes: PositiveMinutes,
});

payload!(TrendPayload {
    bucket: TrendBucket
});

payload!(
    TimeOfDayPayload {
        #[serde(serialize_with = "hh_mm")]
        from: NaiveTime,
        #[serde(serialize_with = "hh_mm")]
        to: NaiveTime,
        #[serde(serialize_with = "zone_name")]
        timezone: Option<Tz>,
    }
    check |p, r| p.from != p.to || r.report(Reason::EmptyWindow)
);

payload!(
    /// `{operator, value}`: iob, cob, reservoir, site_age, sensor_age,
    /// pump_battery, uploader_battery, sensitivity_ratio.
    ComparePayload {
        operator: CmpOp,
        #[serde(serialize_with = "exact_decimal")]
        value: Decimal,
    }
);

payload!(AlertStatePayload {
    alert_id: Uuid,
    state: AlertStateKind,
    for_minutes: Option<i32>,
});

payload!(
    /// `{operator, minutes}`: loop_stale, loop_enaction_stale.
    MinutesComparePayload {
        operator: CmpOp,
        minutes: NonNegativeMinutes,
    }
);

payload!(
    /// `{is_active, for_minutes}`: pump_suspended, override_active,
    /// do_not_disturb.
    ActiveForPayload {
        is_active: bool,
        for_minutes: Option<i32>,
    }
);

payload!(TempBasalPayload {
    metric: TempBasalMetric,
    operator: CmpOp,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
});

payload!(GlucoseBucketPayload {
    buckets: Vec<GlucoseBucket>,
});

payload!(TimeSincePayload {
    operator: CmpOp,
    minutes: NonNegativeMinutes,
});

payload!(DayOfWeekPayload {
    days: Vec<DayOfWeek>,
});

payload!(PumpStatePayload {
    mode: PumpMode,
    is_active: bool,
    for_minutes: Option<i32>,
});

payload!(
    /// `tracker_age`: minutes since the active tracker instance's reference
    /// timestamp, negative before a scheduled event.
    TrackerAgePayload {
        tracker_definition_id: Uuid,
        operator: CmpOp,
        minutes: i32,
    }
);

payload!(
    /// The pump-mode category is `pump_state`'s.
    StateSpanPayload {
        category: StateSpanCategory,
        state: Option<String>,
        is_active: bool,
        for_minutes: Option<i32>,
    }
    check |p, r| p.category != StateSpanCategory::PumpMode
        || r.report(Reason::PumpModeCategory)
);

payload!(SleepSessionPayload { is_active: bool });

fn hh_mm<S: Serializer>(t: &NaiveTime, s: S) -> Result<S::Ok, S::Error> {
    s.collect_str(&t.format("%H:%M"))
}

fn zone_name<S: Serializer>(tz: &Option<Tz>, s: S) -> Result<S::Ok, S::Error> {
    tz.map(Tz::name).serialize(s)
}

/// The reader rejects a 65th nested container anywhere in the document,
/// including inside properties the model ignores (engine-semantics.md §1.1).
const MAX_JSON_DEPTH: usize = 64;

/// One typed frame per value built (node, payload object, condition or enum
/// list); the 64th nested frame is rejected.
const MAX_TYPED_DEPTH: usize = MAX_JSON_DEPTH - 1;

fn within_depth(level: usize) -> bool {
    level <= MAX_TYPED_DEPTH
}

/// Whether the document nests deeper than the reader allows, found without
/// recursing.
fn exceeds_json_depth(root: &Value) -> bool {
    let mut pending = vec![(root, 1usize)];
    while let Some((v, depth)) = pending.pop() {
        let inner = depth.saturating_add(1);
        match v {
            Value::Array(_) | Value::Object(_) if depth > MAX_JSON_DEPTH => return true,
            Value::Array(items) => pending.extend(items.iter().map(|c| (c, inner))),
            Value::Object(fields) => pending.extend(fields.values().map(|c| (c, inner))),
            _ => {}
        }
    }
    false
}

/// Reads one document rooted at `root`: the value when nothing is wrong with
/// it, else every problem found.
fn read_document<T>(
    v: &Value,
    root: &str,
    read: impl FnOnce(&Issues) -> Option<T>,
) -> ParseResult<T> {
    if exceeds_json_depth(v) {
        return Err(Unevaluable(vec![ParseError::new(root, Reason::TooDeep)]));
    }
    let issues = Issues::default();
    let value = read(&issues);
    let issues = issues.0.into_inner();
    match value {
        Some(value) if issues.is_empty() => Ok(value),
        _ => Err(Unevaluable(issues)),
    }
}

/// Reads a rule body: the payload object for `kind`, as its own document.
/// Paths are rooted at the kind's wire name.
///
/// # Errors
/// Every problem that keeps the body from being evaluated (§1.4), including a
/// JSON `null` body.
pub fn parse_payload(kind: ConditionKind, v: &Value) -> ParseResult<Payload> {
    let root = kind.name();
    read_document(v, root, |issues| {
        if v.is_null() {
            issues.report(root, Reason::PayloadMissing);
            None
        } else {
            read_payload(kind, v, 1, root, issues)
        }
    })
}

/// `level` is the payload object's 1-based typed nesting level; `path` is the
/// owning node's condition path.
fn read_payload(
    kind: ConditionKind,
    v: &Value,
    level: usize,
    path: &str,
    issues: &Issues,
) -> Option<Payload> {
    if !within_depth(level) {
        issues.report(path, Reason::TooDeep);
        return None;
    }
    let Value::Object(obj) = v else {
        issues.report(path, Reason::NotAnObject);
        return None;
    };
    let reader = Reader {
        obj,
        level: level.saturating_add(1),
        path,
        issues,
    };
    Payload::read(kind, &reader)
}

/// The `type` of a node value, if it has a string one, for naming its path
/// before it is read.
fn peek_type(v: &Value) -> Option<&str> {
    v.as_object()
        .and_then(|o| o.get("type"))
        .and_then(Value::as_str)
}

/// `level` is the node object's 1-based typed nesting level; `path` its
/// condition path.
fn read_node(v: &Value, level: usize, path: &str, issues: &Issues) -> Option<Node> {
    if !within_depth(level) {
        issues.report(path, Reason::TooDeep);
        return None;
    }
    let Value::Object(obj) = v else {
        issues.report(path, Reason::NotAnObject);
        return None;
    };
    let kind = match obj.get("type") {
        None | Some(Value::Null) => {
            issues.report(path, Reason::TypeMissing);
            return None;
        }
        Some(Value::String(t)) => ConditionKind::from_wire(t),
        Some(_) => {
            issues.report(path, Reason::InvalidField("type"));
            return None;
        }
    };
    let Some(kind) = kind else {
        issues.report(path, Reason::UnknownKind);
        return None;
    };
    if obj
        .iter()
        .any(|(k, v)| !v.is_null() && !matches!(k.as_str(), "type" | "_uid") && k != kind.name())
    {
        issues.report(path, Reason::UnknownField);
    }
    match obj.get(kind.name()) {
        None | Some(Value::Null) => {
            issues.report(path, Reason::PayloadMissing);
            None
        }
        Some(payload) => read_payload(kind, payload, level.saturating_add(1), path, issues),
    }
    .map(Node)
}

/// A condition node: `type` and the payload it names (engine-semantics.md
/// §1.2).
#[derive(Debug, Clone)]
pub struct Node(Payload);

/// The children of a container node (engine-semantics.md §2.1).
#[derive(Debug, Clone, Copy)]
pub enum Container<'a> {
    Composite(&'a CompositePayload),
    Not(&'a NotPayload),
    Sustained(&'a SustainedPayload),
}

impl<'a> Container<'a> {
    /// The children in order.
    pub fn children(self) -> impl Iterator<Item = &'a Node> {
        let (list, single): (&[Node], _) = match self {
            Container::Composite(p) => (p.conditions.as_slice(), None),
            Container::Not(p) => (&[], Some(&*p.child)),
            Container::Sustained(p) => (&[], Some(&*p.child)),
        };
        list.iter().chain(single)
    }
}

impl From<Payload> for Node {
    fn from(payload: Payload) -> Self {
        Node(payload)
    }
}

impl Node {
    /// Reads a full condition node. Paths are rooted at the node's `type`.
    ///
    /// # Errors
    /// Every problem that keeps the node from being evaluated (§1.4).
    pub fn parse(v: &Value) -> ParseResult<Node> {
        Self::parse_rooted(v, peek_type(v).unwrap_or(""))
    }

    /// [`Node::parse`] with the root path segment named by the caller (e.g.
    /// `auto_resolve`).
    ///
    /// # Errors
    /// Every problem that keeps the node from being evaluated (§1.4).
    pub fn parse_rooted(v: &Value, root: &str) -> ParseResult<Node> {
        read_document(v, root, |issues| read_node(v, 1, root, issues))
    }

    #[must_use]
    pub fn kind(&self) -> ConditionKind {
        self.0.kind()
    }

    #[must_use]
    pub fn payload(&self) -> &Payload {
        &self.0
    }

    /// This node's children when it is a container.
    #[must_use]
    pub fn container(&self) -> Option<Container<'_>> {
        match &self.0 {
            Payload::Composite(p) => Some(Container::Composite(p)),
            Payload::Not(p) => Some(Container::Not(p)),
            Payload::Sustained(p) => Some(Container::Sustained(p)),
            _ => None,
        }
    }
}
