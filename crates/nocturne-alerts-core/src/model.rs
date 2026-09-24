//! Condition-tree model: JSON-compatible with the stored `condition_params` /
//! `auto_resolve_params` wire format consumed by the C# engine.
//!
//! Parsing is two passes. The structural pass mirrors System.Text.Json with
//! snake_case naming and case-insensitive property matching: a malformed
//! payload anywhere in the tree is a [`ParseError`], missing fields take the
//! record constructor defaults (`null` for strings, `0` for numbers, `false`
//! for bools), and JSON `null` for a non-nullable value type is an error. The
//! evaluability pass ([`crate::validate`]) then rejects the trees whose
//! evaluation throws (engine-semantics.md §1.4). [`Node::parse`] and
//! [`parse_payload`] run both; the `*_structure` variants run only the first.

use std::collections::HashMap;

use rust_decimal::Decimal;
use serde_json::{Map, Value};
use uuid::Uuid;

use crate::paths::child_path;

/// Why a condition tree is rejected. [`Reason::code`] is the stable wire code;
/// no reason carries a value read from the payload.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
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
}

impl Reason {
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
        }
    }

    /// The payload field the problem is on, when it is on one.
    pub fn field(self) -> Option<&'static str> {
        match self {
            Reason::InvalidField(f) => Some(f),
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
            | Reason::PayloadMissing => None,
        }
    }

    /// Whether evaluating a tree with this problem fails: the rule is skipped
    /// that tick with its timers and tracker untouched (engine-semantics.md
    /// §1.4). The rest evaluate silently false or true and are rejected only
    /// when a rule is saved.
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
            self.path = path.to_owned();
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

/// The `AlertConditionType` discriminator set, in C# enum declaration order
/// (the numeric ordinal matters: `Enum.TryParse` accepts integer strings).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum ConditionKind {
    Threshold,
    RateOfChange,
    SignalLoss,
    Composite,
    Not,
    Sustained,
    Staleness,
    Predicted,
    Trend,
    TimeOfDay,
    Iob,
    Cob,
    Reservoir,
    SiteAge,
    SensorAge,
    AlertState,
    LoopStale,
    LoopEnactionStale,
    PumpSuspended,
    PumpBattery,
    TempBasal,
    UploaderBattery,
    OverrideActive,
    SensitivityRatio,
    DoNotDisturb,
    GlucoseBucket,
    TimeSinceLastCarb,
    TimeSinceLastBolus,
    DayOfWeek,
    PumpState,
    StateSpanActive,
    SleepSessionActive,
    TrackerAge,
}

/// `(kind, wire name, C# enum member name)` in declaration order.
const KINDS: [(ConditionKind, &str, &str); 33] = [
    (ConditionKind::Threshold, "threshold", "Threshold"),
    (
        ConditionKind::RateOfChange,
        "rate_of_change",
        "RateOfChange",
    ),
    (ConditionKind::SignalLoss, "signal_loss", "SignalLoss"),
    (ConditionKind::Composite, "composite", "Composite"),
    (ConditionKind::Not, "not", "Not"),
    (ConditionKind::Sustained, "sustained", "Sustained"),
    (ConditionKind::Staleness, "staleness", "Staleness"),
    (ConditionKind::Predicted, "predicted", "Predicted"),
    (ConditionKind::Trend, "trend", "Trend"),
    (ConditionKind::TimeOfDay, "time_of_day", "TimeOfDay"),
    (ConditionKind::Iob, "iob", "Iob"),
    (ConditionKind::Cob, "cob", "Cob"),
    (ConditionKind::Reservoir, "reservoir", "Reservoir"),
    (ConditionKind::SiteAge, "site_age", "SiteAge"),
    (ConditionKind::SensorAge, "sensor_age", "SensorAge"),
    (ConditionKind::AlertState, "alert_state", "AlertState"),
    (ConditionKind::LoopStale, "loop_stale", "LoopStale"),
    (
        ConditionKind::LoopEnactionStale,
        "loop_enaction_stale",
        "LoopEnactionStale",
    ),
    (
        ConditionKind::PumpSuspended,
        "pump_suspended",
        "PumpSuspended",
    ),
    (ConditionKind::PumpBattery, "pump_battery", "PumpBattery"),
    (ConditionKind::TempBasal, "temp_basal", "TempBasal"),
    (
        ConditionKind::UploaderBattery,
        "uploader_battery",
        "UploaderBattery",
    ),
    (
        ConditionKind::OverrideActive,
        "override_active",
        "OverrideActive",
    ),
    (
        ConditionKind::SensitivityRatio,
        "sensitivity_ratio",
        "SensitivityRatio",
    ),
    (
        ConditionKind::DoNotDisturb,
        "do_not_disturb",
        "DoNotDisturb",
    ),
    (
        ConditionKind::GlucoseBucket,
        "glucose_bucket",
        "GlucoseBucket",
    ),
    (
        ConditionKind::TimeSinceLastCarb,
        "time_since_last_carb",
        "TimeSinceLastCarb",
    ),
    (
        ConditionKind::TimeSinceLastBolus,
        "time_since_last_bolus",
        "TimeSinceLastBolus",
    ),
    (ConditionKind::DayOfWeek, "day_of_week", "DayOfWeek"),
    (ConditionKind::PumpState, "pump_state", "PumpState"),
    (
        ConditionKind::StateSpanActive,
        "state_span_active",
        "StateSpanActive",
    ),
    (
        ConditionKind::SleepSessionActive,
        "sleep_session_active",
        "SleepSessionActive",
    ),
    (ConditionKind::TrackerAge, "tracker_age", "TrackerAge"),
];

impl ConditionKind {
    /// Canonical snake_case wire string (`EnumMember` value in C#).
    pub fn wire(self) -> &'static str {
        KINDS[self.index()].1
    }

    fn index(self) -> usize {
        KINDS
            .iter()
            .position(|(k, _, _)| *k == self)
            .expect("kind present in table")
    }

    /// `AlertConditionTypeNames.FromWireString`: case-insensitive wire lookup.
    pub fn from_wire(s: &str) -> Option<Self> {
        KINDS
            .iter()
            .find(|(_, wire, _)| wire.eq_ignore_ascii_case(s))
            .map(|(k, _, _)| *k)
    }

    /// `ConditionEvaluatorRegistry.GetEvaluator(string)`: wire lookup first,
    /// then `Enum.TryParse(ignoreCase: true)` which matches PascalCase member
    /// names (trimmed) and plain integer ordinals.
    pub fn resolve(type_str: &str) -> Option<Self> {
        if let Some(k) = Self::from_wire(type_str) {
            return Some(k);
        }
        let trimmed = type_str.trim();
        if let Some((k, _, _)) = KINDS
            .iter()
            .find(|(_, _, name)| name.eq_ignore_ascii_case(trimmed))
        {
            return Some(*k);
        }
        if let Ok(i) = trimmed.parse::<i64>()
            && (0..KINDS.len() as i64).contains(&i)
        {
            return Some(KINDS[i as usize].0);
        }
        None
    }
}

// ---------------------------------------------------------------------------
// Enum wire tables (ordinal-indexed; ordinals mirror the C# declaration order)
//
// An integer payload value is the ordinal, so a table that drifts from the C#
// enum silently rebinds integer payloads to the wrong member. The corpus
// generator writes every mirrored enum to `tests/Parity/AlertEngineEnums.json`
// and `enum_tables_match_manifest` pins each table to it.
// ---------------------------------------------------------------------------

pub const TEMP_BASAL_METRIC_NAMES: [&str; 2] = ["rate", "percent_of_scheduled"];
pub const ALERT_CMP_OP_NAMES: [&str; 5] = [">", ">=", "<", "<=", "=="];
pub const DAY_OF_WEEK_NAMES: [&str; 7] = [
    "Sunday",
    "Monday",
    "Tuesday",
    "Wednesday",
    "Thursday",
    "Friday",
    "Saturday",
];
pub const GLUCOSE_BUCKET_NAMES: [&str; 6] = [
    "very_low",
    "low",
    "tight_range",
    "in_range",
    "high",
    "very_high",
];
pub const TREND_BUCKET_NAMES: [&str; 6] = [
    "unknown",
    "rising_fast",
    "rising",
    "flat",
    "falling",
    "falling_fast",
];
pub const PUMP_MODE_NAMES: [&str; 10] = [
    "Automatic",
    "Limited",
    "Manual",
    "Boost",
    "EaseOff",
    "Sleep",
    "Exercise",
    "Liberty",
    "Suspended",
    "Off",
];
pub const STATE_SPAN_CATEGORY_NAMES: [&str; 9] = [
    "PumpMode",
    "PumpConnectivity",
    "Override",
    "Profile",
    "Exercise",
    "Illness",
    "Travel",
    "DataExclusion",
    "TemporaryTarget",
];

/// Case-insensitive enum-name lookup returning the ordinal.
pub fn enum_ordinal(names: &[&str], s: &str) -> Option<i64> {
    names
        .iter()
        .position(|n| n.eq_ignore_ascii_case(s))
        .map(|i| i as i64)
}

// ---------------------------------------------------------------------------
// JSON helpers (System.Text.Json semantics)
// ---------------------------------------------------------------------------

/// Case-insensitive property lookup. With case-insensitive matching STJ binds
/// the last occurrence of a duplicated property name, so scan in order keeping
/// the last hit.
pub fn get_ci<'a>(obj: &'a Map<String, Value>, name: &str) -> Option<&'a Value> {
    let mut found = None;
    for (k, v) in obj {
        if k.eq_ignore_ascii_case(name) {
            found = Some(v);
        }
    }
    found
}

/// Decimal from a JSON number literal the way System.Text.Json reads one
/// (`NumberToDecimal`), with no binary-float round trip. Digits accumulate
/// into the 96-bit mantissa until it would overflow or the scale reaches 28;
/// the next digit rounds half-to-even, where a tie is a `5` followed only by
/// zeros within the first 29 significant digits. A magnitude below the smallest scale-28 step becomes zero rather
/// than an error; `None` when the value exceeds the decimal range or the
/// literal is not a JSON number.
pub fn parse_decimal_literal(s: &str) -> Option<Decimal> {
    const MAX_SCALE: i64 = 28;
    const MAX_MANTISSA: u128 = (1 << 96) - 1;
    // Digits past the 29th reach the rounding step only as a "non-zero tail"
    // flag, so a 5 in the 30th place always rounds up.
    const DIGIT_BUFFER: usize = 29;

    let (negative, unsigned) = match s.strip_prefix('-') {
        Some(rest) => (true, rest),
        None => (false, s),
    };
    let (mantissa_text, exponent) = match unsigned.find(['e', 'E']) {
        Some(i) => (
            &unsigned[..i],
            parse_saturating_exponent(&unsigned[i + 1..])?,
        ),
        None => (unsigned, 0),
    };
    let (int_part, frac_part) = match mantissa_text.split_once('.') {
        Some((int_part, frac_part)) => (int_part, frac_part),
        None => (mantissa_text, ""),
    };
    let all_digits = int_part.bytes().chain(frac_part.bytes());
    if int_part.is_empty() || !all_digits.clone().all(|b| b.is_ascii_digit()) {
        return None;
    }

    let leading_zeros = all_digits.clone().take_while(|&b| b == b'0').count();
    let digits: Vec<u8> = all_digits.skip(leading_zeros).map(|b| b - b'0').collect();
    // `value = 0.d1d2d3… × 10^e`, saturated so an absurd exponent cannot wrap.
    let mut e = (int_part.len() as i64)
        .saturating_sub(leading_zeros as i64)
        .saturating_add(exponent);

    if digits.is_empty() {
        let scale = (frac_part.len() as i64).saturating_sub(exponent);
        return Some(Decimal::from_i128_with_scale(
            0,
            scale.clamp(0, MAX_SCALE) as u32,
        ));
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
            next += 1;
        }
        e -= 1;
    }

    if let Some(&digit) = digits.get(next) {
        let tie_to_even = next < DIGIT_BUFFER
            && digit == 5
            && mantissa.is_multiple_of(2)
            && digits[next + 1..].iter().all(|&d| d == 0);
        if digit >= 5 && !tie_to_even {
            mantissa += 1;
            if mantissa > MAX_MANTISSA {
                mantissa = MAX_MANTISSA / 10 + 1;
                e += 1;
            }
        }
    }

    if e > 0 {
        return None;
    }
    let mut d = if e <= -(MAX_SCALE + 1) {
        Decimal::from_i128_with_scale(0, MAX_SCALE as u32)
    } else {
        Decimal::try_from_i128_with_scale(mantissa as i128, (-e) as u32).ok()?
    };
    d.set_sign_negative(negative && !d.is_zero());
    Some(d)
}

/// A JSON exponent (`[+-]?digits`), saturated to ±`i64::MAX / 2` so later
/// scale arithmetic stays in range.
fn parse_saturating_exponent(s: &str) -> Option<i64> {
    let (negative, digits) = match s.as_bytes().first() {
        Some(b'-') => (true, &s[1..]),
        Some(b'+') => (false, &s[1..]),
        _ => (false, s),
    };
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    let limit = i64::MAX / 2;
    let magnitude = digits.bytes().fold(0i64, |acc, b| {
        acc.saturating_mul(10)
            .saturating_add(i64::from(b - b'0'))
            .min(limit)
    });
    Some(if negative { -magnitude } else { magnitude })
}

pub fn decimal_from_number(n: &serde_json::Number) -> Option<Decimal> {
    parse_decimal_literal(n.as_str())
}

fn invalid(name: &'static str) -> ParseError {
    ParseError::unplaced(Reason::InvalidField(name))
}

fn f_decimal(obj: &Map<String, Value>, name: &'static str) -> ParseResult<Decimal> {
    match get_ci(obj, name) {
        None => Ok(Decimal::ZERO),
        Some(Value::Number(n)) => decimal_from_number(n).ok_or_else(|| invalid(name)),
        Some(_) => Err(invalid(name)),
    }
}

fn i32_from(n: &serde_json::Number) -> Option<i32> {
    n.as_i64().and_then(|v| i32::try_from(v).ok())
}

fn f_i32(obj: &Map<String, Value>, name: &'static str) -> ParseResult<i32> {
    match get_ci(obj, name) {
        None => Ok(0),
        Some(Value::Number(n)) => i32_from(n).ok_or_else(|| invalid(name)),
        Some(_) => Err(invalid(name)),
    }
}

fn f_opt_i32(obj: &Map<String, Value>, name: &'static str) -> ParseResult<Option<i32>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Number(n)) => i32_from(n).map(Some).ok_or_else(|| invalid(name)),
        Some(_) => Err(invalid(name)),
    }
}

fn f_bool(obj: &Map<String, Value>, name: &'static str) -> ParseResult<bool> {
    match get_ci(obj, name) {
        None => Ok(false),
        Some(Value::Bool(b)) => Ok(*b),
        Some(_) => Err(invalid(name)),
    }
}

fn f_string(obj: &Map<String, Value>, name: &'static str) -> ParseResult<Option<String>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::String(s)) => Ok(Some(s.clone())),
        Some(_) => Err(invalid(name)),
    }
}

/// STJ reads a `Guid` only in the 36-character hyphenated form; braced,
/// simple and URN spellings are a `JsonException`.
fn f_uuid(obj: &Map<String, Value>, name: &'static str) -> ParseResult<Uuid> {
    match get_ci(obj, name) {
        None => Ok(Uuid::nil()),
        Some(Value::String(s)) if s.len() == 36 => Uuid::try_parse(s).map_err(|_| invalid(name)),
        Some(_) => Err(invalid(name)),
    }
}

/// Enum field with STJ `JsonStringEnumConverter` semantics: string matched
/// case-insensitively against the wire-name table (falling back to an
/// integer string, surrounding whitespace allowed), or an integer accepted
/// raw (possibly undefined). Either integer form must fit the enum's `int`
/// underlying type. Missing field is the C# constructor default (ordinal 0).
fn f_enum(obj: &Map<String, Value>, name: &'static str, names: &[&str]) -> ParseResult<i64> {
    match get_ci(obj, name) {
        None => Ok(0),
        Some(v) => enum_value(v, names).ok_or_else(|| invalid(name)),
    }
}

fn enum_value(v: &Value, names: &[&str]) -> Option<i64> {
    match v {
        Value::String(s) => {
            enum_ordinal(names, s).or_else(|| s.trim().parse::<i32>().ok().map(i64::from))
        }
        Value::Number(n) => i32_from(n).map(i64::from),
        _ => None,
    }
}

fn f_enum_list(
    obj: &Map<String, Value>,
    name: &'static str,
    names: &[&str],
    level: usize,
) -> ParseResult<Option<Vec<i64>>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Array(items)) => {
            check_typed_level(level)?;
            items
                .iter()
                .map(|v| enum_value(v, names).ok_or_else(|| invalid(name)))
                .collect::<ParseResult<Vec<_>>>()
                .map(Some)
        }
        Some(_) => Err(invalid(name)),
    }
}

// ---------------------------------------------------------------------------
// Payload records (constructor defaults mirror the C# records)
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, Default)]
pub struct ThresholdPayload {
    pub direction: Option<String>,
    pub value: Decimal,
}

#[derive(Debug, Clone, Default)]
pub struct RateOfChangePayload {
    pub direction: Option<String>,
    pub rate: Decimal,
}

#[derive(Debug, Clone, Default)]
pub struct SignalLossPayload {
    pub timeout_minutes: i32,
}

#[derive(Debug, Clone, Default)]
pub struct CompositePayload {
    pub operator: Option<String>,
    /// `None` for an absent or null list, and `None` elements for JSON `null`
    /// entries. Both fail evaluation (engine-semantics.md §1.4), so only
    /// [`Node::parse_structure`] and [`parse_payload_structure`] return them.
    pub conditions: Option<Vec<Option<Node>>>,
}

#[derive(Debug, Clone, Default)]
pub struct NotPayload {
    pub child: Option<Box<Node>>,
}

#[derive(Debug, Clone, Default)]
pub struct SustainedPayload {
    pub minutes: i32,
    pub child: Option<Box<Node>>,
}

#[derive(Debug, Clone, Default)]
pub struct StalenessPayload {
    pub operator: Option<String>,
    pub value: i32,
}

#[derive(Debug, Clone, Default)]
pub struct PredictedPayload {
    pub operator: Option<String>,
    pub value: Decimal,
    pub within_minutes: i32,
}

#[derive(Debug, Clone, Default)]
pub struct TrendPayload {
    pub bucket: Option<String>,
}

#[derive(Debug, Clone, Default)]
pub struct TimeOfDayPayload {
    pub from: Option<String>,
    pub to: Option<String>,
    pub timezone: Option<String>,
}

/// Shared `{operator, value}` shape: iob, cob, reservoir, site_age,
/// sensor_age, pump_battery, uploader_battery, sensitivity_ratio.
#[derive(Debug, Clone, Default)]
pub struct ComparePayload {
    pub operator: Option<String>,
    pub value: Decimal,
}

#[derive(Debug, Clone, Default)]
pub struct AlertStatePayload {
    pub alert_id: Uuid,
    pub state: Option<String>,
    pub for_minutes: Option<i32>,
}

/// Shared `{operator, minutes}` shape: loop_stale, loop_enaction_stale.
#[derive(Debug, Clone, Default)]
pub struct MinutesComparePayload {
    pub operator: Option<String>,
    pub minutes: i32,
}

/// Shared `{is_active, for_minutes}` shape: pump_suspended, override_active,
/// do_not_disturb.
#[derive(Debug, Clone, Default)]
pub struct ActiveForPayload {
    pub is_active: bool,
    pub for_minutes: Option<i32>,
}

#[derive(Debug, Clone, Default)]
pub struct TempBasalPayload {
    /// `TempBasalMetric` ordinal (0 = rate, 1 = percent_of_scheduled); other
    /// values evaluate false, matching the C# `_ => null` switch arm.
    pub metric: i64,
    pub operator: Option<String>,
    pub value: Decimal,
}

#[derive(Debug, Clone, Default)]
pub struct GlucoseBucketPayload {
    pub buckets: Option<Vec<i64>>,
}

#[derive(Debug, Clone, Default)]
pub struct TimeSincePayload {
    /// `AlertComparisonOperator` ordinal (0 `>` … 4 `==`); undefined → false.
    pub operator: i64,
    pub minutes: i32,
}

#[derive(Debug, Clone, Default)]
pub struct DayOfWeekPayload {
    /// `System.DayOfWeek` values: 0 = Sunday … 6 = Saturday, integers raw.
    pub days: Option<Vec<i64>>,
}

#[derive(Debug, Clone, Default)]
pub struct PumpStatePayload {
    /// `PumpModeState` ordinal; missing defaults to 0 (Automatic).
    pub mode: i64,
    pub is_active: bool,
    pub for_minutes: Option<i32>,
}

/// `tracker_age`: minutes since the active tracker instance's reference
/// timestamp (start for duration trackers, scheduled time for event trackers).
#[derive(Debug, Clone, Default)]
pub struct TrackerAgePayload {
    pub tracker_definition_id: Uuid,
    pub operator: Option<String>,
    pub minutes: i32,
}

#[derive(Debug, Clone, Default)]
pub struct StateSpanPayload {
    /// `StateSpanCategory` ordinal; missing defaults to 0 (PumpMode), which
    /// always evaluates false (defence in depth).
    pub category: i64,
    pub state: Option<String>,
    pub is_active: bool,
    pub for_minutes: Option<i32>,
}

#[derive(Debug, Clone, Default)]
pub struct SleepSessionPayload {
    /// Asserts the side of the pre-computed `sleep_session_active` signal to
    /// match: `true` fires while a session is active, `false` while none is.
    pub is_active: bool,
}

/// Parsed kind-specific payload of a [`Node`].
#[derive(Debug, Clone)]
pub enum Payload {
    Threshold(ThresholdPayload),
    RateOfChange(RateOfChangePayload),
    SignalLoss(SignalLossPayload),
    Composite(CompositePayload),
    Not(NotPayload),
    Sustained(SustainedPayload),
    Staleness(StalenessPayload),
    Predicted(PredictedPayload),
    Trend(TrendPayload),
    TimeOfDay(TimeOfDayPayload),
    Compare(ComparePayload),
    AlertState(AlertStatePayload),
    MinutesCompare(MinutesComparePayload),
    ActiveFor(ActiveForPayload),
    TempBasal(TempBasalPayload),
    GlucoseBucket(GlucoseBucketPayload),
    TimeSince(TimeSincePayload),
    DayOfWeek(DayOfWeekPayload),
    PumpState(PumpStatePayload),
    StateSpan(StateSpanPayload),
    SleepSession(SleepSessionPayload),
    TrackerAge(TrackerAgePayload),
}

// ---------------------------------------------------------------------------
// Nesting depth (STJ `MaxDepth` = 64)
// ---------------------------------------------------------------------------

/// The reader rejects a 65th nested container anywhere in the document,
/// including inside properties the model ignores.
const MAX_JSON_DEPTH: usize = 64;

/// The serializer counts one frame per typed value it builds (node, payload
/// object, condition or enum list) and rejects the 64th nested frame.
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
        let is_container = matches!(v, Value::Array(_) | Value::Object(_));
        if is_container && depth > MAX_JSON_DEPTH {
            return Err(ParseError::unplaced(Reason::TooDeep));
        }
        match v {
            Value::Array(items) => pending.extend(items.iter().map(|c| (c, depth + 1))),
            Value::Object(fields) => pending.extend(fields.values().map(|c| (c, depth + 1))),
            _ => {}
        }
    }
    Ok(())
}

/// Parses a rule body: the payload object for `kind`, deserialised as its own
/// document (the stored `condition_params`) and checked for evaluability.
/// Paths are rooted at the kind's wire name. The value must be a JSON object;
/// a JSON `null` is the caller's to handle (it evaluates as a null record,
/// false).
pub fn parse_payload(kind: ConditionKind, v: &Value) -> ParseResult<Payload> {
    let payload = parse_payload_structure(kind, v)?;
    match crate::validate::first_evaluation_fault_in_payload(kind, &payload) {
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
    parse_payload_fields(kind, v, level, path).map_err(|e| e.placed_at(path))
}

fn parse_payload_fields(
    kind: ConditionKind,
    v: &Value,
    level: usize,
    path: &str,
) -> ParseResult<Payload> {
    check_typed_level(level)?;
    let Value::Object(o) = v else {
        return Err(ParseError::unplaced(Reason::NotAnObject));
    };
    let inner = level + 1;
    let p = match kind {
        ConditionKind::Threshold => Payload::Threshold(ThresholdPayload {
            direction: f_string(o, "direction")?,
            value: f_decimal(o, "value")?,
        }),
        ConditionKind::RateOfChange => Payload::RateOfChange(RateOfChangePayload {
            direction: f_string(o, "direction")?,
            rate: f_decimal(o, "rate")?,
        }),
        ConditionKind::SignalLoss => Payload::SignalLoss(SignalLossPayload {
            timeout_minutes: f_i32(o, "timeout_minutes")?,
        }),
        ConditionKind::Composite => Payload::Composite(CompositePayload {
            operator: f_string(o, "operator")?,
            conditions: parse_node_list(o, "conditions", inner, path)?,
        }),
        ConditionKind::Not => Payload::Not(NotPayload {
            child: parse_child(o, "child", inner, path)?,
        }),
        ConditionKind::Sustained => Payload::Sustained(SustainedPayload {
            minutes: f_i32(o, "minutes")?,
            child: parse_child(o, "child", inner, path)?,
        }),
        ConditionKind::Staleness => Payload::Staleness(StalenessPayload {
            operator: f_string(o, "operator")?,
            value: f_i32(o, "value")?,
        }),
        ConditionKind::Predicted => Payload::Predicted(PredictedPayload {
            operator: f_string(o, "operator")?,
            value: f_decimal(o, "value")?,
            within_minutes: f_i32(o, "within_minutes")?,
        }),
        ConditionKind::Trend => Payload::Trend(TrendPayload {
            bucket: f_string(o, "bucket")?,
        }),
        ConditionKind::TimeOfDay => Payload::TimeOfDay(TimeOfDayPayload {
            from: f_string(o, "from")?,
            to: f_string(o, "to")?,
            timezone: f_string(o, "timezone")?,
        }),
        ConditionKind::Iob
        | ConditionKind::Cob
        | ConditionKind::Reservoir
        | ConditionKind::SiteAge
        | ConditionKind::SensorAge
        | ConditionKind::PumpBattery
        | ConditionKind::UploaderBattery
        | ConditionKind::SensitivityRatio => Payload::Compare(ComparePayload {
            operator: f_string(o, "operator")?,
            value: f_decimal(o, "value")?,
        }),
        ConditionKind::AlertState => Payload::AlertState(AlertStatePayload {
            alert_id: f_uuid(o, "alert_id")?,
            state: f_string(o, "state")?,
            for_minutes: f_opt_i32(o, "for_minutes")?,
        }),
        ConditionKind::LoopStale | ConditionKind::LoopEnactionStale => {
            Payload::MinutesCompare(MinutesComparePayload {
                operator: f_string(o, "operator")?,
                minutes: f_i32(o, "minutes")?,
            })
        }
        ConditionKind::PumpSuspended
        | ConditionKind::OverrideActive
        | ConditionKind::DoNotDisturb => Payload::ActiveFor(ActiveForPayload {
            is_active: f_bool(o, "is_active")?,
            for_minutes: f_opt_i32(o, "for_minutes")?,
        }),
        ConditionKind::TempBasal => Payload::TempBasal(TempBasalPayload {
            metric: f_enum(o, "metric", &TEMP_BASAL_METRIC_NAMES)?,
            operator: f_string(o, "operator")?,
            value: f_decimal(o, "value")?,
        }),
        ConditionKind::GlucoseBucket => Payload::GlucoseBucket(GlucoseBucketPayload {
            buckets: f_enum_list(o, "buckets", &GLUCOSE_BUCKET_NAMES, inner)?,
        }),
        ConditionKind::TimeSinceLastCarb | ConditionKind::TimeSinceLastBolus => {
            Payload::TimeSince(TimeSincePayload {
                operator: f_enum(o, "operator", &ALERT_CMP_OP_NAMES)?,
                minutes: f_i32(o, "minutes")?,
            })
        }
        ConditionKind::DayOfWeek => Payload::DayOfWeek(DayOfWeekPayload {
            days: f_enum_list(o, "days", &DAY_OF_WEEK_NAMES, inner)?,
        }),
        ConditionKind::PumpState => Payload::PumpState(PumpStatePayload {
            mode: f_enum(o, "mode", &PUMP_MODE_NAMES)?,
            is_active: f_bool(o, "is_active")?,
            for_minutes: f_opt_i32(o, "for_minutes")?,
        }),
        ConditionKind::StateSpanActive => Payload::StateSpan(StateSpanPayload {
            category: f_enum(o, "category", &STATE_SPAN_CATEGORY_NAMES)?,
            state: f_string(o, "state")?,
            is_active: f_bool(o, "is_active")?,
            for_minutes: f_opt_i32(o, "for_minutes")?,
        }),
        ConditionKind::SleepSessionActive => Payload::SleepSession(SleepSessionPayload {
            is_active: f_bool(o, "is_active")?,
        }),
        ConditionKind::TrackerAge => Payload::TrackerAge(TrackerAgePayload {
            tracker_definition_id: f_uuid(o, "tracker_definition_id")?,
            operator: f_string(o, "operator")?,
            minutes: f_i32(o, "minutes")?,
        }),
    };
    Ok(p)
}

/// The payload a kind evaluates when its payload property is absent or JSON
/// null, or when its `type` is not spelled as the wire name: the C#
/// dispatcher serialises `{}` and the evaluator deserialises a record with
/// constructor defaults.
pub fn default_payload(kind: ConditionKind) -> Payload {
    parse_payload_structure(kind, &Value::Object(Map::new())).expect("empty object parses")
}

/// The `type` of a node value, if it has a string one, for naming its path
/// before it is parsed.
fn peek_type(v: &Value) -> Option<&str> {
    match v {
        Value::Object(o) => get_ci(o, "type").and_then(Value::as_str),
        _ => None,
    }
}

fn parse_child(
    obj: &Map<String, Value>,
    name: &'static str,
    level: usize,
    parent: &str,
) -> ParseResult<Option<Box<Node>>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(v) => {
            let path = child_path(parent, 0, peek_type(v));
            Node::parse_at(v, level, &path).map(|n| Some(Box::new(n)))
        }
    }
}

fn parse_node_list(
    obj: &Map<String, Value>,
    name: &'static str,
    level: usize,
    parent: &str,
) -> ParseResult<Option<Vec<Option<Node>>>> {
    match get_ci(obj, name) {
        None | Some(Value::Null) => Ok(None),
        Some(Value::Array(items)) => {
            check_typed_level(level)?;
            items
                .iter()
                .enumerate()
                .map(|(i, v)| match v {
                    Value::Null => Ok(None),
                    _ => {
                        let path = child_path(parent, i, peek_type(v));
                        Node::parse_at(v, level + 1, &path).map(Some)
                    }
                })
                .collect::<ParseResult<Vec<_>>>()
                .map(Some)
        }
        Some(_) => Err(invalid(name)),
    }
}

/// A parsed `ConditionNode`. Every known payload property present in the JSON
/// is parsed (STJ binds all properties regardless of `type`), keyed by its
/// canonical snake_case name. [`Node::dispatch`] selects the one evaluation
/// reads.
#[derive(Debug, Clone, Default)]
pub struct Node {
    /// Verbatim `type` string from the JSON (case preserved for paths), or
    /// `None` when absent/null.
    pub type_str: Option<String>,
    payloads: HashMap<&'static str, Payload>,
}

impl Node {
    /// Parses a full condition node and checks it is evaluable. Paths are
    /// rooted at the node's verbatim `type`.
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

    /// The structural pass of [`Node::parse`] alone, mirroring
    /// `JsonSerializer.Deserialize<ConditionNode>`: the value must be an
    /// object, `type` a string (or null/absent), and every recognised payload
    /// property must parse.
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
        Self::parse_fields(v, level, path).map_err(|e| e.placed_at(path))
    }

    fn parse_fields(v: &Value, level: usize, path: &str) -> ParseResult<Node> {
        check_typed_level(level)?;
        let Value::Object(obj) = v else {
            return Err(ParseError::unplaced(Reason::NotAnObject));
        };
        let type_str = f_string(obj, "type")?;
        let mut payloads = HashMap::new();
        for (kind, wire, _) in &KINDS {
            if let Some(pv) = get_ci(obj, wire)
                && !pv.is_null()
            {
                let payload = match pv {
                    Value::Object(_) => parse_payload_at(*kind, pv, level + 1, path)?,
                    _ => return Err(invalid(wire)),
                };
                payloads.insert(*wire, payload);
            }
        }
        Ok(Node { type_str, payloads })
    }

    /// Builds the full node the replay/leaf-log path reconstitutes from a DB
    /// row: `{"type": <wire>, "<wire>": <payload>}`. A null payload column
    /// leaves the payload property null (evaluated with defaults).
    pub fn from_rule(kind: ConditionKind, payload: Option<Payload>) -> Node {
        let mut payloads = HashMap::new();
        if let Some(p) = payload {
            payloads.insert(kind.wire(), p);
        }
        Node {
            type_str: Some(kind.wire().to_string()),
            payloads,
        }
    }

    /// The payload bound to the canonical name `name`, if present and non-null.
    pub fn payload(&self, name: &str) -> Option<&Payload> {
        self.payloads.get(name)
    }

    /// What evaluation dispatches on: the resolved kind (`None` for a missing
    /// or unknown `type`) and the stored payload it reads. The payload switch
    /// matches the lowercased `type` against the wire names only, so a kind
    /// resolved through its enum member name or ordinal (e.g.
    /// `"RateOfChange"`) finds no payload and evaluates with
    /// [`default_payload`].
    pub fn dispatch(&self) -> Option<(ConditionKind, Option<&Payload>)> {
        let type_str = self.type_str.as_deref()?;
        let kind = ConditionKind::resolve(type_str)?;
        let payload = if type_str.to_lowercase() == kind.wire() {
            self.payload(kind.wire())
        } else {
            None
        };
        Some((kind, payload))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn manifest() -> Value {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../../tests/Parity/AlertEngineEnums.json"
        );
        let text = std::fs::read_to_string(path).expect("read enum manifest");
        serde_json::from_str(&text).expect("parse enum manifest")
    }

    fn names(manifest: &Value, enum_name: &str) -> Vec<String> {
        manifest[enum_name]
            .as_array()
            .unwrap_or_else(|| panic!("{enum_name} missing from manifest"))
            .iter()
            .map(|v| v.as_str().expect("enum name is a string").to_string())
            .collect()
    }

    #[test]
    fn enum_tables_match_manifest() {
        let m = manifest();
        let tables: [(&str, &[&str]); 7] = [
            ("AlertComparisonOperator", &ALERT_CMP_OP_NAMES),
            ("DayOfWeek", &DAY_OF_WEEK_NAMES),
            ("GlucoseBucket", &GLUCOSE_BUCKET_NAMES),
            ("PumpModeState", &PUMP_MODE_NAMES),
            ("StateSpanCategory", &STATE_SPAN_CATEGORY_NAMES),
            ("TempBasalMetric", &TEMP_BASAL_METRIC_NAMES),
            ("TrendBucket", &TREND_BUCKET_NAMES),
        ];
        for (enum_name, table) in tables {
            assert_eq!(names(&m, enum_name), table, "{enum_name} drifted from C#");
        }
        let wires: Vec<&str> = KINDS.iter().map(|(_, wire, _)| *wire).collect();
        assert_eq!(names(&m, "AlertConditionType"), wires);
        let members: Vec<&str> = KINDS.iter().map(|(_, _, member)| *member).collect();
        assert_eq!(names(&m, "AlertConditionTypeMembers"), members);
    }
}
