//! Node dispatch and container evaluation (engine-semantics.md §2.4, §3).
//!
//! Evaluation cannot fail: a tree reads into the model only when it can be
//! evaluated as written (§1.4).

pub(crate) mod clock;
mod device;
pub(crate) mod elapsed;
pub(crate) mod glucose;
mod insulin;
mod signal;
mod spans;

use chrono::{DateTime, TimeDelta, Utc};
use rust_decimal::Decimal;
use uuid::Uuid;

use crate::clock_anomaly::ClockAnomalies;
use crate::context::SensorContext;
use crate::enums::CompositeOp;
use crate::facts::ContextFact;
use crate::model::{Margined, Node, Payload};
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
    pub(crate) fn limit(&self, p: &impl Margined) -> Decimal {
        p.limit(self.excursion_open, self.negated)
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
