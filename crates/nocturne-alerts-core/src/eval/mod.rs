//! Node dispatch and container evaluation (engine-semantics.md §2.4, §3).
//!
//! Evaluation cannot fail: a tree reads into the model only when it can be
//! evaluated as written (§1.4).

pub(crate) mod clock;
pub(crate) mod determination;
mod device;
pub(crate) mod elapsed;
pub(crate) mod glucose;
mod insulin;
#[cfg(test)]
mod limit_tests;
mod signal;
mod spans;

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;
use uuid::Uuid;

use crate::clock_anomaly::ClockAnomalies;
use crate::context::SensorContext;
use crate::enums::{CompositeOp, ForecastCurve, LimitFact};
use crate::facts::ContextFact;
use crate::model::{LiteralLimit, Node, Payload};
use crate::paths::node_child_path;
use crate::sustained::{TimerStore, eval_sustained};
use crate::truth::Truth;
use crate::unknown::{UnknownFacts, UnknownReason};
use elapsed::Elapsed;

pub use glucose::{DEFAULT_GLUCOSE_FRESHNESS, MAX_GLUCOSE_FRESHNESS_MINUTES};

/// Per-evaluation environment: the clock instant, the rule whose timers are
/// keyed with its glucose freshness bound and when it was enabled, the sensor
/// context, the mutable timer store, and the anchors read ahead of the clock.
pub struct Env<'a> {
    pub(crate) now: DateTime<Utc>,
    pub(crate) rule_id: Uuid,
    pub(crate) glucose_freshness: TimeDelta,
    pub(crate) enabled_at: Option<DateTime<Utc>>,
    /// The lower bound of a `sustained` start at a path with no recorded
    /// state: the rule's `enabled_at` on its first evaluation, else `now`
    /// (`None`).
    pub(crate) first_evaluation_bound: Option<DateTime<Utc>>,
    /// Whether the rule's excursion is open (engine-semantics.md §3, clear
    /// margin), so a margined leaf compares against its widened limit.
    pub(crate) excursion_open: bool,
    /// Whether the node being evaluated is under an odd number of `not`s, so
    /// a margin moves its limit toward the leaf's false side.
    pub(crate) negated: bool,
    pub(crate) ctx: &'a SensorContext,
    pub(crate) timers: &'a mut TimerStore,
    clock_anomaly: ClockAnomalies,
}

impl<'a> Env<'a> {
    /// With the default glucose freshness bound, [`DEFAULT_GLUCOSE_FRESHNESS`],
    /// and no enablement instant.
    pub fn new(
        now: DateTime<Utc>,
        rule_id: Uuid,
        ctx: &'a SensorContext,
        timers: &'a mut TimerStore,
    ) -> Self {
        Self {
            now,
            rule_id,
            glucose_freshness: DEFAULT_GLUCOSE_FRESHNESS,
            enabled_at: None,
            first_evaluation_bound: None,
            excursion_open: false,
            negated: false,
            ctx,
            timers,
            clock_anomaly: ClockAnomalies::default(),
        }
    }

    #[must_use]
    pub fn with_glucose_freshness(self, glucose_freshness: TimeDelta) -> Self {
        Self {
            glucose_freshness,
            ..self
        }
    }

    #[must_use]
    pub fn with_enabled_at(self, enabled_at: Option<DateTime<Utc>>) -> Self {
        Self { enabled_at, ..self }
    }

    /// See [`Env::first_evaluation_bound`].
    #[must_use]
    pub(crate) fn with_first_evaluation_bound(self, bound: Option<DateTime<Utc>>) -> Self {
        Self {
            first_evaluation_bound: bound,
            ..self
        }
    }

    /// See [`Env::excursion_open`].
    #[must_use]
    pub(crate) fn with_excursion_open(self, excursion_open: bool) -> Self {
        Self {
            excursion_open,
            ..self
        }
    }

    /// The limit `p` compares against in this environment.
    pub(crate) fn limit(&self, p: &impl LiteralLimit) -> Decimal {
        p.widened(p.literal(), self.excursion_open, self.negated)
    }

    /// Every anchor the evaluations in this environment read beyond the
    /// tolerance ahead of `now` (engine-semantics.md §5.4).
    #[must_use]
    pub fn clock_anomaly(&self) -> &ClockAnomalies {
        &self.clock_anomaly
    }

    pub(crate) fn into_clock_anomaly(self) -> ClockAnomalies {
        self.clock_anomaly
    }

    /// `e` at `now`, noting its anchor when it is beyond the tolerance ahead.
    pub(crate) fn measure(&mut self, e: Elapsed) -> Truth {
        self.clock_anomaly.note(e.ahead(self.now));
        e.truth(self.now)
    }
}

/// A node's truth; when it is true, the instant it has held since where the
/// node knows one (engine-semantics.md §3), where a `sustained` ancestor's run
/// begins; when it is unknown, the facts whose unknown reached it (§2.4).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct Held {
    pub(crate) truth: Truth,
    pub(crate) since: Option<DateTime<Utc>>,
    pub(crate) unknown: UnknownFacts,
}

impl Held {
    /// `since` is kept only on a true node.
    pub(crate) fn since(truth: Truth, since: Option<DateTime<Utc>>) -> Self {
        Held {
            truth,
            since: since.filter(|_| truth.is_true()),
            unknown: UnknownFacts::default(),
        }
    }

    /// `unknown` is kept only on an unknown node.
    #[must_use]
    pub(crate) fn because(self, unknown: UnknownFacts) -> Self {
        Held {
            unknown: if self.truth.is_unknown() {
                unknown
            } else {
                UnknownFacts::default()
            },
            ..self
        }
    }

    pub(crate) fn unknown(fact: ContextFact, reason: UnknownReason) -> Self {
        Held::from(Truth::Unknown).because(UnknownFacts::of(fact, reason))
    }
}

/// What a true `predicted`, `eventual_glucose` or `carbs_required` leaf, or
/// a true `threshold` whose limit names a fact, met its comparison with
/// (engine-semantics.md §3): for `predicted`, the curve and the offset of
/// the point; for a limit from a fact, the limit compared against, the
/// fact and the rule's offset to it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MetPoint {
    pub curve: Option<ForecastCurve>,
    pub offset_minutes: Option<i32>,
    pub value: Decimal,
    pub limit: Option<Decimal>,
    pub limit_fact: Option<LimitFact>,
    /// `None` for an offset of zero.
    pub limit_offset: Option<Decimal>,
}

impl MetPoint {
    pub(crate) fn on_curve(curve: ForecastCurve, offset_minutes: i32, value: Decimal) -> Self {
        Self {
            curve: Some(curve),
            offset_minutes: Some(offset_minutes),
            ..Self::value(value)
        }
    }

    pub(crate) fn value(value: Decimal) -> Self {
        Self {
            curve: None,
            offset_minutes: None,
            value,
            limit: None,
            limit_fact: None,
            limit_offset: None,
        }
    }

    /// With `limit`, its fact and the offset to it when it came from one; a
    /// literal limit is the rule's own and is not repeated.
    pub(crate) fn against(self, limit: glucose::Effective) -> Self {
        match limit.fact {
            Some(fact) => Self {
                limit: Some(limit.value),
                limit_fact: Some(fact),
                limit_offset: (!limit.offset.is_zero()).then_some(limit.offset),
                ..self
            },
            None => self,
        }
    }

    /// `{"leaf_id", "kind", "curve"?, "offset_minutes"?, "value", "limit"?,
    /// "limit_fact"?, "limit_offset"?}`, each decimal as the exact number it
    /// prints as.
    #[must_use]
    pub fn to_json(&self, leaf_id: usize, kind: crate::model::ConditionKind) -> serde_json::Value {
        let number = |d: Decimal| {
            d.to_string()
                .parse::<serde_json::Number>()
                .map_or(serde_json::Value::Null, serde_json::Value::Number)
        };
        let mut o = serde_json::Map::new();
        o.insert("leaf_id".into(), leaf_id.into());
        o.insert("kind".into(), crate::enums::WireEnum::name(kind).into());
        if let Some(curve) = self.curve {
            o.insert("curve".into(), crate::enums::WireEnum::name(curve).into());
        }
        if let Some(offset) = self.offset_minutes {
            o.insert("offset_minutes".into(), offset.into());
        }
        o.insert("value".into(), number(self.value));
        if let Some(limit) = self.limit {
            o.insert("limit".into(), number(limit));
        }
        if let Some(fact) = self.limit_fact {
            o.insert(
                "limit_fact".into(),
                crate::enums::WireEnum::name(fact).into(),
            );
        }
        if let Some(offset) = self.limit_offset {
            o.insert("limit_offset".into(), number(offset));
        }
        serde_json::Value::Object(o)
    }
}

/// Whether `kind` reads the loop's determination only while it is fresh
/// (engine-semantics.md §3).
#[must_use]
pub(crate) fn judges_determination_freshness(kind: crate::model::ConditionKind) -> bool {
    use crate::model::ConditionKind;
    matches!(
        kind,
        ConditionKind::EventualGlucose | ConditionKind::CarbsRequired
    )
}

/// The point a leaf met its comparison with, when it is a true leaf of a kind
/// that names one; `None` for every other leaf.
pub(crate) fn met_point(payload: &Payload, env: &mut Env) -> Option<MetPoint> {
    match payload {
        Payload::Threshold(p) => glucose::threshold_met(p, env).1,
        Payload::Predicted(p) => glucose::predicted_met(p, env).1,
        Payload::EventualGlucose(_) | Payload::CarbsRequired(_) => {
            determination::determined(payload, env).1
        }
        _ => None,
    }
}

impl From<Truth> for Held {
    fn from(truth: Truth) -> Self {
        Held::since(truth, None)
    }
}

/// A truth that is unknown only by `fact` being `reason`.
pub(crate) fn unknown_by(truth: Truth, fact: ContextFact, reason: UnknownReason) -> Held {
    Held::from(truth).because(UnknownFacts::of(fact, reason))
}

/// Evaluates a condition node at `path`.
pub fn eval_node(node: &Node, path: &str, env: &mut Env) -> Truth {
    eval_held(node, path, env).truth
}

/// Evaluates a payload as the node at `path`.
pub fn eval_payload(payload: &Payload, path: &str, env: &mut Env) -> Truth {
    eval_payload_held(payload, path, env).truth
}

/// Evaluates a payload as the node at `path`, with the facts that leave it
/// unknown.
pub fn eval_payload_unknown(payload: &Payload, path: &str, env: &mut Env) -> (Truth, UnknownFacts) {
    let held = eval_payload_held(payload, path, env);
    (held.truth, held.unknown)
}

pub(crate) fn eval_held(node: &Node, path: &str, env: &mut Env) -> Held {
    eval_payload_held(node.payload(), path, env)
}

fn eval_payload_held(payload: &Payload, path: &str, env: &mut Env) -> Held {
    match payload {
        Payload::Threshold(p) => glucose::threshold(p, env),
        Payload::RateOfChange(p) => glucose::rate_of_change(p, env),
        Payload::Composite(p) => composite(p, path, env),
        Payload::Not(p) => not(p, path, env),
        Payload::Sustained(p) => eval_sustained(p, path, env),
        Payload::Predicted(p) => glucose::predicted(p, env),
        Payload::Trend(p) => glucose::trend(p, env),
        Payload::TimeOfDay(p) => clock::time_of_day(p, env).into(),
        Payload::Iob(p) => insulin::iob(p, env),
        Payload::Cob(p) => insulin::cob(p, env),
        Payload::Reservoir(p) => insulin::reservoir(p, env),
        Payload::PumpBattery(p) => device::pump_battery(p, env),
        Payload::TempBasal(p) => insulin::temp_basal(p, env),
        Payload::UploaderBattery(p) => device::uploader_battery(p, env),
        Payload::TransmitterBattery(p) => device::transmitter_battery(p, env),
        Payload::SensitivityRatio(p) => device::sensitivity_ratio(p, env),
        Payload::GlucoseBucket(p) => glucose::glucose_bucket(p, env),
        Payload::DayOfWeek(p) => clock::day_of_week(p, env).into(),
        Payload::SleepSessionActive(p) => spans::sleep_session_active(p, env).into(),
        Payload::EventualGlucose(_) | Payload::CarbsRequired(_) => {
            determination::determined(payload, env).0
        }
        Payload::SignalLoss(_)
        | Payload::Staleness(_)
        | Payload::SiteAge(_)
        | Payload::SensorAge(_)
        | Payload::AlertState(_)
        | Payload::LoopStale(_)
        | Payload::LoopEnactionStale(_)
        | Payload::PumpSuspended(_)
        | Payload::OverrideActive(_)
        | Payload::DoNotDisturb(_)
        | Payload::TimeSinceLastCarb(_)
        | Payload::TimeSinceLastBolus(_)
        | Payload::PumpState(_)
        | Payload::StateSpanActive(_)
        | Payload::SensorState(_)
        | Payload::TrackerAge(_) => elapsed::clock_leaf(payload, env.ctx, env.enabled_at, env.now)
            .map_or(Truth::False.into(), |leaf| {
                if let Some(e) = leaf.elapsed() {
                    env.measure(e);
                }
                let held = leaf.held(env.now);
                match payload {
                    // Another rule's trigger and acknowledgement are its
                    // excursion's, not a run of the facts this rule reads.
                    Payload::AlertState(_) => Held {
                        since: None,
                        ..held
                    },
                    _ => held,
                }
            }),
    }
}

/// `and` / `or` evaluate every child in document order, whatever an earlier
/// one returned, so each sustained timer tracks its own condition wherever it
/// sits. They combine in Kleene's logic. A true `and` has held since the
/// latest of its children's instants when every child knows one; a true `or`
/// since the earliest instant a true child knows, since that child alone has
/// held it true from then. An unknown result is unknown by every unknown
/// child: none is false (`and`) or true (`or`), so each reaches it.
fn composite(p: &crate::model::CompositePayload, path: &str, env: &mut Env) -> Held {
    let results: Vec<Held> = p
        .conditions
        .iter()
        .enumerate()
        .map(|(i, child)| eval_held(child, &node_child_path(path, i, child), env))
        .collect();
    let truths = results.iter().map(|h| h.truth);
    let held = match p.operator {
        CompositeOp::And => Held::since(
            Truth::all(truths),
            results
                .iter()
                .map(|h| h.since)
                .collect::<Option<Vec<_>>>()
                .and_then(|starts| starts.into_iter().max()),
        ),
        CompositeOp::Or => Held::since(
            Truth::any(truths),
            results.iter().filter_map(|h| h.since).min(),
        ),
    };
    held.because(
        results
            .iter()
            .fold(UnknownFacts::default(), |all, h| all.union(h.unknown)),
    )
}

/// The child inverted; unknown stays unknown, by the child's facts. The
/// child's instant is when it began to hold, not when it stopped, so `not`
/// knows none. The child is evaluated with its polarity turned, so a margin
/// under it still widens the rule's truth.
fn not(p: &crate::model::NotPayload, path: &str, env: &mut Env) -> Held {
    env.negated = !env.negated;
    let child = eval_held(&p.child, &node_child_path(path, 0, &p.child), env);
    env.negated = !env.negated;
    Held::from(!child.truth).because(child.unknown)
}
