//! Sustained-condition timers, which record their mutations for the host to
//! persist, and the `sustained` container (engine-semantics.md §3).

use std::collections::{BTreeMap, HashMap};

use chrono::{DateTime, TimeDelta, Utc};
use uuid::Uuid;

use crate::clock_anomaly::ClockAnchor;
use crate::eval::elapsed::Elapsed;
use crate::eval::{Env, Held, eval_held};
use crate::model::SustainedPayload;
use crate::paths::node_child_path;
use crate::truth::Truth;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum TimerOpKind {
    /// A run started at `at`.
    Set,
    /// No run: the child was last found not true at `at`.
    Clear,
}

impl TimerOpKind {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            TimerOpKind::Set => "set",
            TimerOpKind::Clear => "clear",
        }
    }
}

/// A recorded timer mutation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimerOp {
    pub kind: TimerOpKind,
    pub path: String,
    pub at: DateTime<Utc>,
}

/// How long a recorded not-true instant stands for the not-true evaluations
/// after it, so a child that stays false writes its state at most this often
/// (engine-semantics.md §3).
pub const NOT_TRUE_LEASE: TimeDelta = TimeDelta::minutes(5);

/// What a `sustained` path holds between evaluations.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TimerState {
    /// The child's run started at this instant.
    Running(DateTime<Utc>),
    /// No run; the last evaluation that found the child not true was at this
    /// instant, so no run starts before it.
    NotTrue(DateTime<Utc>),
}

impl TimerState {
    fn op(self, path: &str) -> TimerOp {
        let (kind, at) = match self {
            TimerState::Running(at) => (TimerOpKind::Set, at),
            TimerState::NotTrue(at) => (TimerOpKind::Clear, at),
        };
        TimerOp {
            kind,
            path: path.to_owned(),
            at,
        }
    }
}

/// Each `sustained` path's state keyed by rule and condition path. An op is
/// recorded only when a path's state changes.
#[derive(Debug, Default)]
pub struct TimerStore {
    timers: HashMap<Uuid, BTreeMap<String, TimerState>>,
    log: Vec<TimerOp>,
}

impl TimerStore {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    fn get(&self, rule_id: Uuid, path: &str) -> Option<TimerState> {
        self.timers.get(&rule_id)?.get(path).copied()
    }

    pub(crate) fn get_first_true(&self, rule_id: Uuid, path: &str) -> Option<DateTime<Utc>> {
        match self.get(rule_id, path)? {
            TimerState::Running(at) => Some(at),
            TimerState::NotTrue(_) => None,
        }
    }

    pub(crate) fn not_true_at(&self, rule_id: Uuid, path: &str) -> Option<DateTime<Utc>> {
        match self.get(rule_id, path)? {
            TimerState::NotTrue(at) => Some(at),
            TimerState::Running(_) => None,
        }
    }

    /// Whether `rule_id` has any recorded state.
    pub(crate) fn has_any(&self, rule_id: Uuid) -> bool {
        self.timers.get(&rule_id).is_some_and(|t| !t.is_empty())
    }

    fn record(&mut self, rule_id: Uuid, path: &str, state: TimerState) {
        let previous = self
            .timers
            .entry(rule_id)
            .or_default()
            .insert(path.to_owned(), state);
        if previous != Some(state) {
            self.log.push(state.op(path));
        }
    }

    pub(crate) fn set_first_true(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        self.record(rule_id, path, TimerState::Running(at));
    }

    /// Ends any run at `path` and records `at` as its last not-true
    /// evaluation, unless a not-true instant less than [`NOT_TRUE_LEASE`]
    /// before `at`, or after it (a clock that went back), is recorded already:
    /// that one stands for this evaluation (see [`TimerStore::not_true_bound`]).
    pub(crate) fn mark_not_true(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        let leased = self
            .not_true_at(rule_id, path)
            .is_some_and(|recorded| at.signed_duration_since(recorded) < NOT_TRUE_LEASE);
        if !leased {
            self.record(rule_id, path, TimerState::NotTrue(at));
        }
    }

    /// The earliest a run at `path` may start at `now` by its recorded
    /// not-true instant: every not-true evaluation after it, up to
    /// [`NOT_TRUE_LEASE`] later, went unrecorded, so the last lies no later
    /// than the lease's end, and none at all lies after `now`.
    pub(crate) fn not_true_bound(
        &self,
        rule_id: Uuid,
        path: &str,
        now: DateTime<Utc>,
    ) -> Option<DateTime<Utc>> {
        self.not_true_at(rule_id, path).map(|recorded| {
            recorded
                .checked_add_signed(NOT_TRUE_LEASE)
                .map_or(now, |end| end.min(now))
        })
    }

    /// The ops recorded since the last drain.
    pub fn drain_ops(&mut self) -> Vec<TimerOp> {
        std::mem::take(&mut self.log)
    }

    /// Loads a persisted state without recording an op.
    pub fn seed(&mut self, rule_id: Uuid, path: &str, state: TimerState) {
        self.timers
            .entry(rule_id)
            .or_default()
            .insert(path.to_owned(), state);
    }

    /// `rule_id`'s states by path, in path order: what a host persists.
    pub fn snapshot_for_rule(&self, rule_id: Uuid) -> impl Iterator<Item = (&str, TimerState)> {
        self.timers
            .get(&rule_id)
            .into_iter()
            .flatten()
            .map(|(path, &state)| (path.as_str(), state))
    }
}

/// The child evaluates first. False ends the run keyed by this node's path
/// and records `now` as its last not-true evaluation, within the lease of
/// [`TimerStore::mark_not_true`]. Unknown is unknown and
/// leaves the path as it is: not knowing neither breaks a run nor starts one,
/// nor moves the bound. True with no run starts one at the instant the child
/// has held since, when it knows one, else at `now`, never before the path's
/// [`TimerStore::not_true_bound`] (on a rule's first evaluation, the rule's
/// `enabled_at`; with neither, `now`) and never after `now`; the node then
/// holds once `minutes` fractional minutes have passed since the start, so a
/// run that began long enough ago holds at once. A start beyond the tolerance
/// ahead of `now`, set by a clock that has since gone back, is unknown and
/// kept: the run is neither complete nor broken (§5.4), and names no fact,
/// since the start is the engine's own state. A true node has held since its
/// start plus `minutes`; an unknown child leaves it unknown by the child's
/// facts.
pub(crate) fn eval_sustained(p: &SustainedPayload, path: &str, env: &mut Env) -> Held {
    let now = env.now;
    let child = eval_held(&p.child, &node_child_path(path, 0, &p.child), env);
    match child.truth {
        Truth::Unknown => child,
        Truth::False => {
            env.timers.mark_not_true(env.rule_id, path, now);
            Truth::False.into()
        }
        Truth::True => {
            let start = env
                .timers
                .get_first_true(env.rule_id, path)
                .unwrap_or_else(|| {
                    let bound = env
                        .timers
                        .not_true_bound(env.rule_id, path, now)
                        .or(env.first_evaluation_bound)
                        .unwrap_or(now);
                    let start = child.since.unwrap_or(now).max(bound).min(now);
                    env.timers.set_first_true(env.rule_id, path, start);
                    start
                });
            let run = Elapsed::held_for(start, p.minutes.get(), ClockAnchor::Sustained);
            let truth = env.measure(run);
            Held::since(truth, run.began(now))
        }
    }
}
