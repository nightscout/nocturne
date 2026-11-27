//! The migration of a stored schema 1 condition tree to schema 2
//! (engine-semantics.md §1.5), the one schema the engine reads.
//!
//! It rewrites only the operands whose shape changed and names the schema at
//! the root; everything else is copied as it is, so a problem the tree had is
//! still there for the reader to report. It fails, and the tree stays as it
//! was, where it cannot read what it has to rewrite or descend into: such a
//! tree was not evaluable as schema 1 either.

use rust_decimal::Decimal;
use rust_decimal::prelude::ToPrimitive;
use serde_json::{Map, Number, Value};

use crate::enums::{CmpOp, WireEnum, wire_enum};
use crate::model::{
    CURRENT_SCHEMA, ConditionKind, ParseError, Reason, SCHEMA_VERSION, decimal_from_number,
};
use crate::paths::child_path;

wire_enum! {
    /// Schema 1 `threshold.direction`.
    enum ThresholdDirection {
        Above = "above",
        Below = "below",
    }
}

wire_enum! {
    /// Schema 1 `rate_of_change.direction`.
    enum RateDirection {
        Rising = "rising",
        Falling = "falling",
    }
}

/// How a tree is stored, which decides the object its schema is named on.
#[derive(Debug, Clone, Copy)]
pub enum TreeRoot<'a> {
    /// A rule body: the payload object for the rule's `condition_type`, the
    /// kind's wire name, which also roots its paths.
    Body(&'a str),
    /// A full node (`auto_resolve_params`, a snooze condition), its paths
    /// rooted at the given string.
    Node(&'a str),
}

/// A migrated tree.
#[derive(Debug, Clone, PartialEq)]
pub struct Migrated {
    pub tree: Value,
    /// Whether a duration was not a whole number of minutes and was rounded,
    /// so the rule should be reviewed.
    pub rounded: bool,
}

/// What the migration made of a tree.
#[derive(Debug, Clone, PartialEq)]
pub enum Migration {
    /// The tree already names [`CURRENT_SCHEMA`]; nothing to do.
    Current,
    Migrated(Migrated),
}

/// Rewrites a schema 1 tree as schema 2. A tree that already names schema 2
/// is [`Migration::Current`], so the migration can run again.
///
/// # Errors
/// The first place the tree cannot be read as schema 1: a node that is not an
/// object, has no `type` naming a kind or no payload object; a container
/// without its children; an operand to rewrite that is malformed; an operand
/// that already has its schema 2 name; or a root naming another schema.
pub fn migrate_v1_to_v2(tree: &Value, root: TreeRoot<'_>) -> Result<Migration, ParseError> {
    let (root_path, object) = match root {
        TreeRoot::Body(kind) => (kind, tree),
        TreeRoot::Node(path) => (path, tree),
    };
    let Value::Object(obj) = object else {
        return Err(ParseError::new(
            root_path,
            if object.is_null() {
                Reason::PayloadMissing
            } else {
                Reason::NotAnObject
            },
        ));
    };
    match obj.get(SCHEMA_VERSION) {
        None => {}
        Some(v) if v.as_u64() == Some(CURRENT_SCHEMA) => return Ok(Migration::Current),
        Some(_) => return Err(ParseError::new(root_path, Reason::UnsupportedSchema)),
    }
    let mut m = Migrator { rounded: false };
    let body = match root {
        TreeRoot::Body(kind) => {
            let kind = ConditionKind::from_wire(kind)
                .ok_or_else(|| ParseError::new(root_path, Reason::UnknownKind))?;
            m.payload(kind, obj, root_path, false)?
        }
        TreeRoot::Node(_) => m.node_object(obj, root_path, false)?,
    };
    let mut named = Map::with_capacity(body.len().saturating_add(1));
    named.insert(SCHEMA_VERSION.to_owned(), Value::from(CURRENT_SCHEMA));
    named.extend(body);
    Ok(Migration::Migrated(Migrated {
        tree: Value::Object(named),
        rounded: m.rounded,
    }))
}

struct Migrator {
    rounded: bool,
}

impl Migrator {
    /// A child node; `negated` is whether it is under an odd number of
    /// `not`s.
    fn node(&mut self, v: &Value, path: &str, negated: bool) -> Result<Value, ParseError> {
        match v {
            Value::Object(obj) => self.node_object(obj, path, negated).map(Value::Object),
            Value::Null => Err(ParseError::new(path, Reason::ConditionMissing)),
            _ => Err(ParseError::new(path, Reason::NotAnObject)),
        }
    }

    fn node_object(
        &mut self,
        obj: &Map<String, Value>,
        path: &str,
        negated: bool,
    ) -> Result<Map<String, Value>, ParseError> {
        let kind = match obj.get("type") {
            None | Some(Value::Null) => return Err(ParseError::new(path, Reason::TypeMissing)),
            Some(Value::String(t)) => ConditionKind::from_wire(t)
                .ok_or_else(|| ParseError::new(path, Reason::UnknownKind))?,
            Some(_) => return Err(ParseError::new(path, Reason::InvalidField("type"))),
        };
        let Some(Value::Object(payload)) = obj.get(kind.name()) else {
            return Err(ParseError::new(path, Reason::PayloadMissing));
        };
        let migrated = self.payload(kind, payload, path, negated)?;
        let mut out = obj.clone();
        out.insert(kind.name().to_owned(), Value::Object(migrated));
        Ok(out)
    }

    fn payload(
        &mut self,
        kind: ConditionKind,
        p: &Map<String, Value>,
        path: &str,
        negated: bool,
    ) -> Result<Map<String, Value>, ParseError> {
        let mut out = p.clone();
        let fail = |reason| Err(ParseError::new(path, reason));
        match kind {
            ConditionKind::Composite => {
                let Some(Value::Array(children)) = p.get("conditions") else {
                    return fail(Reason::ListEmpty("conditions"));
                };
                let migrated = children
                    .iter()
                    .enumerate()
                    .map(|(i, child)| {
                        let child_path = child_path(path, i, peek_type(child));
                        self.node(child, &child_path, negated)
                    })
                    .collect::<Result<Vec<_>, _>>()?;
                out.insert("conditions".to_owned(), Value::Array(migrated));
            }
            ConditionKind::Not | ConditionKind::Sustained => {
                let Some(child) = p.get("child").filter(|c| !c.is_null()) else {
                    return fail(Reason::FieldMissing("child"));
                };
                let child_path = child_path(path, 0, peek_type(child));
                let negated = negated ^ matches!(kind, ConditionKind::Not);
                out.insert("child".to_owned(), self.node(child, &child_path, negated)?);
            }
            ConditionKind::Threshold => {
                if let Some(direction) = take(&mut out, "direction", "operator", path)? {
                    let operator = match word::<ThresholdDirection>(&direction, "direction", path)?
                    {
                        ThresholdDirection::Below => CmpOp::Lt,
                        ThresholdDirection::Above => CmpOp::Gt,
                    };
                    out.insert("operator".to_owned(), Value::from(operator.name()));
                }
            }
            ConditionKind::RateOfChange => {
                let direction = take(&mut out, "direction", "operator", path)?;
                let rate = take(&mut out, "rate", "value", path)?;
                let direction = direction
                    .map(|d| word::<RateDirection>(&d, "direction", path))
                    .transpose()?;
                if let Some(direction) = direction {
                    out.insert(
                        "operator".to_owned(),
                        Value::from(match direction {
                            RateDirection::Falling => CmpOp::Le.name(),
                            RateDirection::Rising => CmpOp::Ge.name(),
                        }),
                    );
                }
                match (direction, rate) {
                    (_, None) => {}
                    (Some(RateDirection::Rising), Some(rate)) => {
                        out.insert("value".to_owned(), rate);
                    }
                    (Some(RateDirection::Falling), Some(rate)) => {
                        let rate = number(&rate, "rate", path)?;
                        out.insert("value".to_owned(), exact(-rate, "rate", path)?);
                    }
                    (None, Some(_)) => return fail(Reason::FieldMissing("direction")),
                }
            }
            ConditionKind::Staleness => {
                if let Some(value) = take(&mut out, "value", "minutes", path)? {
                    out.insert("minutes".to_owned(), value);
                }
            }
            ConditionKind::SiteAge => self.age(&mut out, 60, path, negated)?,
            ConditionKind::SensorAge => self.age(&mut out, 1440, path, negated)?,
            ConditionKind::SignalLoss
            | ConditionKind::Predicted
            | ConditionKind::Trend
            | ConditionKind::TimeOfDay
            | ConditionKind::Iob
            | ConditionKind::Cob
            | ConditionKind::Reservoir
            | ConditionKind::AlertState
            | ConditionKind::LoopStale
            | ConditionKind::LoopEnactionStale
            | ConditionKind::PumpSuspended
            | ConditionKind::PumpBattery
            | ConditionKind::TempBasal
            | ConditionKind::UploaderBattery
            | ConditionKind::OverrideActive
            | ConditionKind::SensitivityRatio
            | ConditionKind::DoNotDisturb
            | ConditionKind::GlucoseBucket
            | ConditionKind::TimeSinceLastCarb
            | ConditionKind::TimeSinceLastBolus
            | ConditionKind::DayOfWeek
            | ConditionKind::PumpState
            | ConditionKind::StateSpanActive
            | ConditionKind::SleepSessionActive
            | ConditionKind::TrackerAge
            | ConditionKind::TransmitterBattery
            | ConditionKind::SensorState
            | ConditionKind::EventualGlucose
            | ConditionKind::CarbsRequired
            | ConditionKind::GlucoseChange
            | ConditionKind::GlucoseCount
            | ConditionKind::GlucoseExtreme => {}
        }
        Ok(out)
    }

    /// `site_age` hours or `sensor_age` days (`per_unit` minutes each) as
    /// whole `minutes`. A value that is not a whole number of minutes is
    /// rounded so the leaf, read through the `not`s above it, is true at least
    /// wherever it was: down for `>`, `>=` and `==`, up for `<` and `<=`, the
    /// other way round under an odd number of `not`s.
    fn age(
        &mut self,
        out: &mut Map<String, Value>,
        per_unit: u32,
        path: &str,
        negated: bool,
    ) -> Result<(), ParseError> {
        let Some(value) = take(out, "value", "minutes", path)? else {
            return Ok(());
        };
        let minutes = number(&value, "value", path)?
            .checked_mul(Decimal::from(per_unit))
            .ok_or_else(|| ParseError::new(path, Reason::InvalidField("value")))?;
        let whole = if minutes.fract().is_zero() {
            minutes
        } else {
            let operator = out
                .get("operator")
                .ok_or_else(|| ParseError::new(path, Reason::FieldMissing("operator")))
                .and_then(|v| word::<CmpOp>(v, "operator", path))?;
            self.rounded = true;
            let up = matches!(operator, CmpOp::Lt | CmpOp::Le) != negated;
            if up { minutes.ceil() } else { minutes.floor() }
        };
        let whole = whole.to_i32().unwrap_or_else(|| {
            self.rounded = true;
            if whole.is_sign_negative() {
                i32::MIN
            } else {
                i32::MAX
            }
        });
        out.insert("minutes".to_owned(), Value::from(whole));
        Ok(())
    }
}

/// Removes the schema 1 operand `old`, refusing a payload that already has
/// its schema 2 name `new`, which schema 1 did not read.
fn take(
    out: &mut Map<String, Value>,
    old: &'static str,
    new: &'static str,
    path: &str,
) -> Result<Option<Value>, ParseError> {
    if out.get(new).is_some_and(|v| !v.is_null()) {
        return Err(ParseError::new(path, Reason::UnknownField));
    }
    Ok(out.shift_remove(old).filter(|v| !v.is_null()))
}

fn word<E: WireEnum>(v: &Value, name: &'static str, path: &str) -> Result<E, ParseError> {
    let text = v
        .as_str()
        .ok_or_else(|| ParseError::new(path, Reason::InvalidField(name)))?;
    E::from_name(text).ok_or_else(|| ParseError::new(path, Reason::UnknownValue(name)))
}

fn number(v: &Value, name: &'static str, path: &str) -> Result<Decimal, ParseError> {
    v.as_number()
        .and_then(decimal_from_number)
        .ok_or_else(|| ParseError::new(path, Reason::InvalidField(name)))
}

/// `d` as the JSON number it prints as; zero prints without a sign.
fn exact(d: Decimal, name: &'static str, path: &str) -> Result<Value, ParseError> {
    let d = if d.is_zero() { Decimal::ZERO } else { d };
    d.to_string()
        .parse::<Number>()
        .map(Value::Number)
        .map_err(|_| ParseError::new(path, Reason::InvalidField(name)))
}

fn peek_type(v: &Value) -> Option<&str> {
    v.as_object()
        .and_then(|o| o.get("type"))
        .and_then(Value::as_str)
}
