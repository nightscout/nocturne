//! Condition-tree model: JSON-compatible with the stored `condition_params` /
//! `auto_resolve_params` wire format (engine-semantics.md §1).
//!
//! Parsing is two passes. The structural pass reads JSON the way
//! engine-semantics.md §1.1 specifies: snake_case names matched
//! case-insensitively, a malformed payload anywhere in the tree is a
//! [`ParseError`], missing fields take their defaults (`null` for strings,
//! `0` for numbers and enums, `false` for bools), and JSON `null` for a
//! non-nullable value is an error. The evaluability pass ([`crate::validate`])
//! then rejects the trees whose evaluation fails (§1.4). [`Node::parse`] and
//! [`parse_payload`] run both; the `*_structure` variants run only the first.

use std::borrow::Cow;

use rust_decimal::Decimal;
use serde::{Serialize, Serializer};
use serde_json::{Map, Value};
use uuid::Uuid;

use crate::enums::{
    AlertStateKind, CmpOp, CompositeOp, DayOfWeek, EnumValue, GlucoseBucket, PumpMode,
    RateDirection, Spelled, StateSpanCategory, TempBasalMetric, ThresholdDirection, TrendBucket,
    WireEnum,
};
use crate::paths::child_path;

/// Why a condition tree is rejected. [`Reason::code`] is the stable wire code;
/// no reason carries a value read from the payload.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum Reason {
    /// A node or root payload is not a JSON object.
    NotAnObject,
    /// The named field has the wrong JSON kind or an unrepresentable value.
    InvalidField(&'static str),
    /// Nested deeper than the reader's `MaxDepth`.
    TooDeep,
    /// A node with no `type`.
    TypeMissing,
    /// A `null` slot in a composite's `conditions`.
    ConditionMissing,
    /// A composite with no `conditions` list.
    ConditionsMissing,
    /// A composite with conditions but no `operator`.
    OperatorMissing,
    /// A `threshold` or `rate_of_change` with no `direction`.
    DirectionMissing,
    /// An `alert_state` with no `state`.
    StateMissing,
    /// A rule body whose `condition_params` is JSON `null`.
    PayloadMissing,
    /// A `type` naming no condition kind.
    UnknownKind,
    /// A `type` that resolves to a kind but is not its wire name.
    NonCanonicalType,
    UnknownOperator,
    UnknownDirection,
    UnknownState,
    /// A composite with an empty `conditions` list.
    ConditionsEmpty,
    /// A `not` or `sustained` with no `child`.
    ChildMissing,
    /// A `sustained` whose `minutes` is zero or negative.
    MinutesNotPositive,
    /// A property no condition node or payload of its kind has.
    UnknownField,
    /// A required operand is absent, so it would read as its default.
    FieldMissing(&'static str),
    /// An enum operand given as an ordinal no member has, or a word naming
    /// no member.
    UnknownValue(&'static str),
    /// A `time_of_day` bound that is not `HH:mm`.
    InvalidTime(&'static str),
    /// A `time_of_day` whose `from` equals its `to`.
    EmptyWindow,
    /// A `glucose_bucket` or `day_of_week` list that is absent or empty.
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
            Reason::ConditionsMissing => "conditions_missing",
            Reason::OperatorMissing => "operator_missing",
            Reason::DirectionMissing => "direction_missing",
            Reason::StateMissing => "state_missing",
            Reason::PayloadMissing => "payload_missing",
            Reason::UnknownKind => "unknown_kind",
            Reason::NonCanonicalType => "non_canonical_type",
            Reason::UnknownOperator => "unknown_operator",
            Reason::UnknownDirection => "unknown_direction",
            Reason::UnknownState => "unknown_state",
            Reason::ConditionsEmpty => "conditions_empty",
            Reason::ChildMissing => "child_missing",
            Reason::MinutesNotPositive => "minutes_not_positive",
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
            | Reason::ListEmpty(f) => Some(f),
            Reason::PumpModeCategory => Some("category"),
            Reason::TypeMissing | Reason::UnknownKind | Reason::NonCanonicalType => Some("type"),
            Reason::ConditionsMissing | Reason::ConditionsEmpty => Some("conditions"),
            Reason::OperatorMissing | Reason::UnknownOperator => Some("operator"),
            Reason::DirectionMissing | Reason::UnknownDirection => Some("direction"),
            Reason::StateMissing | Reason::UnknownState => Some("state"),
            Reason::ChildMissing => Some("child"),
            Reason::MinutesNotPositive => Some("minutes"),
            Reason::NotAnObject
            | Reason::TooDeep
            | Reason::ConditionMissing
            | Reason::PayloadMissing
            | Reason::UnknownField
            | Reason::EmptyWindow => None,
        }
    }

    /// Whether evaluating a tree with this problem fails: the rule is skipped
    /// that tick with its timers and tracker untouched (engine-semantics.md
    /// §1.4). The rest evaluate silently false or true and are rejected only
    /// when a rule is saved.
    #[must_use]
    pub fn fails_evaluation(self) -> bool {
        matches!(
            self,
            Reason::NotAnObject
                | Reason::InvalidField(_)
                | Reason::TooDeep
                | Reason::TypeMissing
                | Reason::ConditionMissing
                | Reason::ConditionsMissing
                | Reason::OperatorMissing
                | Reason::DirectionMissing
                | Reason::StateMissing
        )
    }
}

/// A rejected condition tree: the problem and the condition path
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

    /// A field-level error raised before the node's path is known.
    fn unplaced(reason: Reason) -> Self {
        Self::new(String::new(), reason)
    }

    fn placed_at(mut self, path: &str) -> Self {
        if self.path.is_empty() {
            path.clone_into(&mut self.path);
        }
        self
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

pub type ParseResult<T> = Result<T, ParseError>;

/// Declares [`ConditionKind`] (in the host enum's declaration order, the
/// ordinal a `type` may be written as) and [`Payload`], one variant per kind.
macro_rules! condition_kinds {
    ($($kind:ident = $wire:literal => $payload:ident),+ $(,)?) => {
        crate::enums::wire_enum! {
            /// The condition kinds. [`WireEnum::name`] is the wire name.
            pub enum ConditionKind { $($kind = $wire),+ }
        }

        impl ConditionKind {
            /// The host enum's member name.
            #[must_use]
            pub fn member(self) -> &'static str {
                match self {
                    $(Self::$kind => stringify!($kind)),+
                }
            }
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

            /// What `kind` reads when its payload property is absent, null, or
            /// under a `type` not spelled as the wire name: every field at its
            /// default.
            #[must_use]
            pub fn default_for(kind: ConditionKind) -> Self {
                match kind {
                    $(ConditionKind::$kind => Self::$kind($payload::default())),+
                }
            }

            /// The payload property names this kind reads.
            #[must_use]
            pub fn fields(&self) -> &'static [&'static str] {
                match self {
                    $(Self::$kind(_) => $payload::FIELDS),+
                }
            }

            fn read(kind: ConditionKind, r: &Reader<'_>) -> ParseResult<Self> {
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
    #[must_use]
    pub fn wire(self) -> &'static str {
        self.name()
    }

    /// The kind whose wire name is `s`, ignoring ASCII case.
    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        Self::from_name(s)
    }

    /// The kind a node's `type` names (engine-semantics.md §1.2): its wire
    /// name, else its member name or ordinal, trimmed and ignoring ASCII case.
    #[must_use]
    pub fn resolve(type_str: &str) -> Option<Self> {
        let trimmed = type_str.trim();
        Self::from_wire(type_str)
            .or_else(|| {
                Self::ALL
                    .iter()
                    .copied()
                    .find(|k| k.member().eq_ignore_ascii_case(trimmed))
            })
            .or_else(|| trimmed.parse().ok().and_then(Self::from_ordinal))
    }
}

/// Case-insensitive property lookup; a duplicated name binds its last
/// occurrence.
pub(crate) fn get_ci<'a>(obj: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    obj.iter()
        .rev()
        .find(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v)
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

/// The object a payload's fields are read from. `level` is the typed nesting
/// level of the values inside it; `path` is the owning node's condition path.
struct Reader<'a> {
    obj: &'a Map<String, Value>,
    level: usize,
    path: &'a str,
}

impl Reader<'_> {
    fn get(&self, name: &str) -> Option<&Value> {
        get_ci(self.obj, name)
    }
}

fn invalid(name: &'static str) -> ParseError {
    ParseError::unplaced(Reason::InvalidField(name))
}

/// A payload field type and how it reads from JSON (engine-semantics.md §1.1).
trait Field: Sized {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self>;
}

fn i32_from(n: &serde_json::Number) -> Option<i32> {
    n.as_i64().and_then(|v| i32::try_from(v).ok())
}

impl Field for Decimal {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None => Ok(Decimal::ZERO),
            Some(Value::Number(n)) => decimal_from_number(n).ok_or_else(|| invalid(name)),
            Some(_) => Err(invalid(name)),
        }
    }
}

impl Field for i32 {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None => Ok(0),
            Some(Value::Number(n)) => i32_from(n).ok_or_else(|| invalid(name)),
            Some(_) => Err(invalid(name)),
        }
    }
}

impl Field for Option<i32> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None | Some(Value::Null) => Ok(None),
            Some(_) => i32::read(r, name).map(Some),
        }
    }
}

impl Field for bool {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None => Ok(false),
            Some(Value::Bool(b)) => Ok(*b),
            Some(_) => Err(invalid(name)),
        }
    }
}

impl Field for Option<String> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        read_string(r.obj, name)
    }
}

fn read_string(obj: &Map<String, Value>, name: &'static str) -> ParseResult<Option<String>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone())),
        Some(_) => Err(invalid(name)),
    }
}

/// Only the 36-character hyphenated form reads; braced, simple and URN
/// spellings are errors.
impl Field for Uuid {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None => Ok(Uuid::nil()),
            Some(Value::String(s)) if s.len() == 36 => {
                Uuid::try_parse(s).map_err(|_| invalid(name))
            }
            Some(_) => Err(invalid(name)),
        }
    }
}

impl<T: WireEnum> Field for Spelled<T> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        Option::<String>::read(r, name).map(Spelled::new)
    }
}

/// A string matches a name ignoring ASCII case, falling back to an integer
/// string (surrounding whitespace allowed); a number is taken raw. Either
/// integer form must fit an `i32`.
fn enum_value<E: WireEnum>(v: &Value) -> Option<EnumValue<E>> {
    match v {
        Value::String(s) => E::from_name(s)
            .map(EnumValue::Known)
            .or_else(|| s.trim().parse().ok().map(EnumValue::from_ordinal)),
        Value::Number(n) => i32_from(n).map(EnumValue::from_ordinal),
        _ => None,
    }
}

impl<E: WireEnum> Field for EnumValue<E> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None => Ok(Self::default()),
            Some(v) => enum_value(v).ok_or_else(|| invalid(name)),
        }
    }
}

impl<E: WireEnum> Field for Option<Vec<EnumValue<E>>> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None | Some(Value::Null) => Ok(None),
            Some(Value::Array(items)) => {
                check_typed_level(r.level)?;
                items
                    .iter()
                    .map(|v| enum_value(v).ok_or_else(|| invalid(name)))
                    .collect::<ParseResult<Vec<_>>>()
                    .map(Some)
            }
            Some(_) => Err(invalid(name)),
        }
    }
}

impl Field for Option<Box<Node>> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None | Some(Value::Null) => Ok(None),
            Some(v) => {
                let path = child_path(r.path, 0, peek_type(v));
                Node::parse_at(v, r.level, &path).map(|n| Some(Box::new(n)))
            }
        }
    }
}

impl Field for Option<Vec<Option<Node>>> {
    fn read(r: &Reader<'_>, name: &'static str) -> ParseResult<Self> {
        match r.get(name) {
            None | Some(Value::Null) => Ok(None),
            Some(Value::Array(items)) => {
                check_typed_level(r.level)?;
                items
                    .iter()
                    .enumerate()
                    .map(|(i, v)| match v {
                        Value::Null => Ok(None),
                        _ => {
                            let path = child_path(r.path, i, peek_type(v));
                            Node::parse_at(v, r.level.saturating_add(1), &path).map(Some)
                        }
                    })
                    .collect::<ParseResult<Vec<_>>>()
                    .map(Some)
            }
            Some(_) => Err(invalid(name)),
        }
    }
}

/// Declares a payload record whose fields read, in declaration order, from
/// the JSON properties of the same names.
macro_rules! payload {
    ($(#[$meta:meta])* $name:ident { $($(#[$field_meta:meta])* $field:ident: $ty:ty),* $(,)? }) => {
        $(#[$meta])*
        #[derive(Debug, Clone, Default, Serialize)]
        pub struct $name {
            $($(#[$field_meta])* pub $field: $ty),*
        }

        impl $name {
            const FIELDS: &'static [&'static str] = &[$(stringify!($field)),*];

            fn read(r: &Reader<'_>) -> ParseResult<Self> {
                Ok(Self { $($field: Field::read(r, stringify!($field))?),* })
            }
        }
    };
}

payload!(ThresholdPayload {
    direction: Spelled<ThresholdDirection>,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
});

payload!(RateOfChangePayload {
    direction: Spelled<RateDirection>,
    #[serde(serialize_with = "exact_decimal")]
    rate: Decimal,
});

payload!(SignalLossPayload {
    timeout_minutes: i32
});

payload!(CompositePayload {
    operator: Spelled<CompositeOp>,
    /// `None` for an absent or null list, and `None` elements for JSON `null`
    /// entries. Both fail evaluation (engine-semantics.md §1.4), so only
    /// [`Node::parse_structure`] and [`parse_payload_structure`] return them.
    #[serde(skip)]
    conditions: Option<Vec<Option<Node>>>,
});

payload!(NotPayload {
    #[serde(skip)]
    child: Option<Box<Node>>,
});

payload!(SustainedPayload {
    minutes: i32,
    #[serde(skip)]
    child: Option<Box<Node>>,
});

payload!(StalenessPayload {
    operator: Spelled<CmpOp>,
    value: i32,
});

payload!(PredictedPayload {
    operator: Spelled<CmpOp>,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
    within_minutes: i32,
});

payload!(TrendPayload {
    bucket: Spelled<TrendBucket>,
});

payload!(TimeOfDayPayload {
    from: Option<String>,
    to: Option<String>,
    timezone: Option<String>,
});

payload!(
    /// `{operator, value}`: iob, cob, reservoir, site_age, sensor_age,
    /// pump_battery, uploader_battery, sensitivity_ratio.
    ComparePayload {
        operator: Spelled<CmpOp>,
        #[serde(serialize_with = "exact_decimal")]
        value: Decimal,
    }
);

payload!(AlertStatePayload {
    alert_id: Uuid,
    state: Spelled<AlertStateKind>,
    for_minutes: Option<i32>,
});

payload!(
    /// `{operator, minutes}`: loop_stale, loop_enaction_stale.
    MinutesComparePayload {
        operator: Spelled<CmpOp>,
        minutes: i32,
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
    metric: EnumValue<TempBasalMetric>,
    operator: Spelled<CmpOp>,
    #[serde(serialize_with = "exact_decimal")]
    value: Decimal,
});

payload!(GlucoseBucketPayload {
    buckets: Option<Vec<EnumValue<GlucoseBucket>>>,
});

payload!(TimeSincePayload {
    operator: EnumValue<CmpOp>,
    minutes: i32,
});

payload!(DayOfWeekPayload {
    days: Option<Vec<EnumValue<DayOfWeek>>>,
});

payload!(PumpStatePayload {
    mode: EnumValue<PumpMode>,
    is_active: bool,
    for_minutes: Option<i32>,
});

payload!(
    /// `tracker_age`: minutes since the active tracker instance's reference
    /// timestamp.
    TrackerAgePayload {
        tracker_definition_id: Uuid,
        operator: Spelled<CmpOp>,
        minutes: i32,
    }
);

payload!(StateSpanPayload {
    category: EnumValue<StateSpanCategory>,
    state: Option<String>,
    is_active: bool,
    for_minutes: Option<i32>,
});

payload!(SleepSessionPayload { is_active: bool });

/// The reader rejects a 65th nested container anywhere in the document,
/// including inside properties the model ignores (engine-semantics.md §1.1).
const MAX_JSON_DEPTH: usize = 64;

/// One typed frame per value built (node, payload object, condition or enum
/// list); the 64th nested frame is rejected.
const MAX_TYPED_DEPTH: usize = MAX_JSON_DEPTH - 1;

fn check_typed_level(level: usize) -> ParseResult<()> {
    if level > MAX_TYPED_DEPTH {
        Err(ParseError::unplaced(Reason::TooDeep))
    } else {
        Ok(())
    }
}

/// Rejects a document nested deeper than the reader allows, without
/// recursing.
fn check_json_depth(root: &Value) -> ParseResult<()> {
    let mut pending = vec![(root, 1usize)];
    while let Some((v, depth)) = pending.pop() {
        let inner = depth.saturating_add(1);
        match v {
            Value::Array(_) | Value::Object(_) if depth > MAX_JSON_DEPTH => {
                return Err(ParseError::unplaced(Reason::TooDeep));
            }
            Value::Array(items) => pending.extend(items.iter().map(|c| (c, inner))),
            Value::Object(fields) => pending.extend(fields.values().map(|c| (c, inner))),
            _ => {}
        }
    }
    Ok(())
}

/// Parses a rule body: the payload object for `kind`, read as its own
/// document and checked for evaluability. Paths are rooted at the kind's wire
/// name. A JSON `null` is the caller's to handle: it evaluates false.
pub fn parse_payload(kind: ConditionKind, v: &Value) -> ParseResult<Payload> {
    let payload = parse_payload_structure(kind, v)?;
    match crate::validate::first_evaluation_fault_in_payload(&payload) {
        Some(e) => Err(e),
        None => Ok(payload),
    }
}

/// The structural pass of [`parse_payload`] alone.
pub fn parse_payload_structure(kind: ConditionKind, v: &Value) -> ParseResult<Payload> {
    let root = kind.wire();
    check_json_depth(v).map_err(|e| e.placed_at(root))?;
    parse_payload_at(kind, v, 1, root)
}

/// `level` is the payload object's 1-based typed nesting level; `path` is the
/// owning node's condition path.
fn parse_payload_at(
    kind: ConditionKind,
    v: &Value,
    level: usize,
    path: &str,
) -> ParseResult<Payload> {
    let read = || {
        check_typed_level(level)?;
        let Value::Object(obj) = v else {
            return Err(ParseError::unplaced(Reason::NotAnObject));
        };
        let level = level.saturating_add(1);
        Payload::read(kind, &Reader { obj, level, path })
    };
    read().map_err(|e| e.placed_at(path))
}

/// The `type` of a node value, if it has a string one, for naming its path
/// before it is parsed.
fn peek_type(v: &Value) -> Option<&str> {
    match v {
        Value::Object(o) => get_ci(o, "type").and_then(Value::as_str),
        _ => None,
    }
}

/// A parsed condition node. Every payload property present in the JSON is
/// parsed, whatever the `type`; [`Node::dispatch`] selects the one evaluation
/// reads.
#[derive(Debug, Clone, Default)]
pub struct Node {
    /// The `type` as written (case preserved for paths), or `None` when
    /// absent or null.
    pub type_str: Option<String>,
    payloads: Vec<Payload>,
}

/// The unwrapped form of a container node (engine-semantics.md §2.1).
#[derive(Debug, Clone, Copy)]
pub enum Container<'a> {
    Composite(&'a CompositePayload, &'a [Option<Node>]),
    Not(&'a Node),
    Sustained(&'a SustainedPayload, &'a Node),
}

impl<'a> Container<'a> {
    /// The child slots in order; `None` is a JSON-null composite slot.
    pub fn children(self) -> impl Iterator<Item = Option<&'a Node>> {
        let (list, single): (&[Option<Node>], _) = match self {
            Container::Composite(_, conditions) => (conditions, None),
            Container::Not(child) | Container::Sustained(_, child) => (&[], Some(child)),
        };
        list.iter().map(Option::as_ref).chain(single.map(Some))
    }
}

impl Node {
    /// Parses a full condition node and checks it is evaluable. Paths are
    /// rooted at the node's `type` as written.
    pub fn parse(v: &Value) -> ParseResult<Node> {
        Self::parse_rooted(v, peek_type(v).unwrap_or(""))
    }

    /// [`Node::parse`] with the root path segment named by the caller (e.g.
    /// `auto_resolve`).
    pub fn parse_rooted(v: &Value, root: &str) -> ParseResult<Node> {
        let node = Self::parse_structure_rooted(v, root)?;
        match crate::validate::first_evaluation_fault(&node, root) {
            Some(e) => Err(e),
            None => Ok(node),
        }
    }

    /// The structural pass of [`Node::parse`] alone: the value must be an
    /// object, `type` a string (or null/absent), and every payload property
    /// must parse.
    pub fn parse_structure(v: &Value) -> ParseResult<Node> {
        Self::parse_structure_rooted(v, peek_type(v).unwrap_or(""))
    }

    pub fn parse_structure_rooted(v: &Value, root: &str) -> ParseResult<Node> {
        check_json_depth(v).map_err(|e| e.placed_at(root))?;
        Self::parse_at(v, 1, root)
    }

    /// `level` is the node object's 1-based typed nesting level; `path` its
    /// condition path.
    fn parse_at(v: &Value, level: usize, path: &str) -> ParseResult<Node> {
        let read = || {
            check_typed_level(level)?;
            let Value::Object(obj) = v else {
                return Err(ParseError::unplaced(Reason::NotAnObject));
            };
            let type_str = read_string(obj, "type")?;
            let mut payloads = Vec::new();
            for &kind in ConditionKind::ALL {
                match get_ci(obj, kind.wire()) {
                    None | Some(Value::Null) => {}
                    Some(pv @ Value::Object(_)) => {
                        payloads.push(parse_payload_at(kind, pv, level.saturating_add(1), path)?);
                    }
                    Some(_) => return Err(invalid(kind.wire())),
                }
            }
            Ok(Node { type_str, payloads })
        };
        read().map_err(|e| e.placed_at(path))
    }

    /// A rule body as a full node: `{"type": <wire>, "<wire>": <payload>}`,
    /// with no payload property for a null body.
    #[must_use]
    pub fn from_rule(kind: ConditionKind, payload: Option<Payload>) -> Node {
        Node {
            type_str: Some(kind.wire().to_owned()),
            payloads: payload.into_iter().collect(),
        }
    }

    /// The payload property for `kind`, if present and non-null.
    #[must_use]
    pub fn payload(&self, kind: ConditionKind) -> Option<&Payload> {
        self.payloads.iter().find(|p| p.kind() == kind)
    }

    /// The payload evaluation reads: `None` for a missing or unknown `type`.
    /// A kind reached through its member name or ordinal (e.g.
    /// `"RateOfChange"`) reads [`Payload::default_for`], not its stored
    /// payload (engine-semantics.md §1.2).
    #[must_use]
    pub fn dispatch(&self) -> Option<Cow<'_, Payload>> {
        let type_str = self.type_str.as_deref()?;
        let kind = ConditionKind::resolve(type_str)?;
        let stored = if type_str.to_lowercase() == kind.wire() {
            self.payload(kind)
        } else {
            None
        };
        Some(stored.map_or_else(|| Cow::Owned(Payload::default_for(kind)), Cow::Borrowed))
    }

    /// This node's children when it is a container: a composite, not or
    /// sustained whose stored payload has its children. Any other node,
    /// including a container missing them, is a leaf (§2.2).
    #[must_use]
    pub fn container(&self) -> Option<Container<'_>> {
        match self.dispatch()? {
            Cow::Borrowed(Payload::Composite(p)) => p
                .conditions
                .as_deref()
                .map(|conditions| Container::Composite(p, conditions)),
            Cow::Borrowed(Payload::Not(p)) => p.child.as_deref().map(Container::Not),
            Cow::Borrowed(Payload::Sustained(p)) => p
                .child
                .as_deref()
                .map(|child| Container::Sustained(p, child)),
            _ => None,
        }
    }
}
