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
use uuid::Uuid;

use crate::context::SensorContext;
use crate::enums::CompositeOp;
use crate::model::{Node, Payload};
use crate::paths::node_child_path;
use crate::sustained::{TimerStore, eval_sustained};
use crate::truth::Truth;

pub use glucose::{DEFAULT_GLUCOSE_FRESHNESS, MAX_GLUCOSE_FRESHNESS_MINUTES};

/// Per-evaluation environment: the clock instant, the rule whose timers are
/// keyed with its glucose freshness bound and when it was enabled, the sensor
/// context, and the mutable timer store.
pub struct Env<'a> {
    pub(crate) now: DateTime<Utc>,
    pub(crate) rule_id: Uuid,
    pub(crate) glucose_freshness: TimeDelta,
    pub(crate) enabled_at: Option<DateTime<Utc>>,
    pub(crate) ctx: &'a SensorContext,
    pub(crate) timers: &'a mut TimerStore,
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
            ctx,
            timers,
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
}

/// Evaluates a condition node at `path`.
pub fn eval_node(node: &Node, path: &str, env: &mut Env) -> Truth {
    eval_payload(node.payload(), path, env)
}

/// Evaluates a payload as the node at `path`.
pub fn eval_payload(payload: &Payload, path: &str, env: &mut Env) -> Truth {
    match payload {
        Payload::Threshold(p) => glucose::threshold(p, env),
        Payload::RateOfChange(p) => glucose::rate_of_change(p, env),
        Payload::Composite(p) => composite(p, path, env),
        Payload::Not(p) => not(p, path, env),
        Payload::Sustained(p) => eval_sustained(p, path, env),
        Payload::Predicted(p) => glucose::predicted(p, env),
        Payload::Trend(p) => glucose::trend(p, env),
        Payload::TimeOfDay(p) => clock::time_of_day(p, env),
        Payload::Iob(p) => insulin::iob(p, env),
        Payload::Cob(p) => insulin::cob(p, env),
        Payload::Reservoir(p) => insulin::reservoir(p, env),
        Payload::PumpBattery(p) => device::pump_battery(p, env),
        Payload::TempBasal(p) => insulin::temp_basal(p, env),
        Payload::UploaderBattery(p) => device::uploader_battery(p, env),
        Payload::TransmitterBattery(p) => device::transmitter_battery(p, env),
        Payload::SensitivityRatio(p) => device::sensitivity_ratio(p, env),
        Payload::GlucoseBucket(p) => glucose::glucose_bucket(p, env),
        Payload::DayOfWeek(p) => clock::day_of_week(p, env),
        Payload::SleepSessionActive(p) => spans::sleep_session_active(p, env),
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
        | Payload::TrackerAge(_) => elapsed::clock_leaf(payload, env.ctx, env.enabled_at)
            .map_or(Truth::False, |leaf| leaf.holds(env.now)),
    }
}

/// `and` / `or` evaluate every child in document order, whatever an earlier
/// one returned, so each sustained timer tracks its own condition wherever it
/// sits. They combine in Kleene's logic.
fn composite(p: &crate::model::CompositePayload, path: &str, env: &mut Env) -> Truth {
    let results: Vec<Truth> = p
        .conditions
        .iter()
        .enumerate()
        .map(|(i, child)| eval_node(child, &node_child_path(path, i, child), env))
        .collect();
    match p.operator {
        CompositeOp::And => Truth::all(results),
        CompositeOp::Or => Truth::any(results),
    }
}

/// The child inverted; unknown stays unknown.
fn not(p: &crate::model::NotPayload, path: &str, env: &mut Env) -> Truth {
    !eval_node(&p.child, &node_child_path(path, 0, &p.child), env)
}
