//! Sustained-condition timers, which record their mutations for the host to
//! persist, and the `sustained` container (engine-semantics.md §3).

use std::collections::{BTreeMap, HashMap};

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::eval::{Env, eval_node};
use crate::model::SustainedPayload;
use crate::paths::node_child_path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum TimerOpKind {
    Set,
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

/// A recorded timer mutation. `at` is present only for `set`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimerOp {
    pub kind: TimerOpKind,
    pub path: String,
    pub at: Option<DateTime<Utc>>,
}

/// First-true instants keyed by rule and condition path. A clear is recorded
/// only when a timer existed.
#[derive(Debug, Default)]
pub struct TimerStore {
    timers: HashMap<Uuid, BTreeMap<String, DateTime<Utc>>>,
    log: Vec<TimerOp>,
}

impl TimerStore {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    pub(crate) fn get_first_true(&self, rule_id: Uuid, path: &str) -> Option<DateTime<Utc>> {
        self.timers.get(&rule_id)?.get(path).copied()
    }

    pub(crate) fn set_first_true(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        self.seed(rule_id, path, at);
        self.log.push(TimerOp {
            kind: TimerOpKind::Set,
            path: path.to_owned(),
            at: Some(at),
        });
    }

    pub(crate) fn clear(&mut self, rule_id: Uuid, path: &str) {
        let removed = self
            .timers
            .get_mut(&rule_id)
            .and_then(|timers| timers.remove(path));
        if removed.is_some() {
            self.log.push(TimerOp {
                kind: TimerOpKind::Clear,
                path: path.to_owned(),
                at: None,
            });
        }
    }

    /// The ops recorded since the last drain.
    pub fn drain_ops(&mut self) -> Vec<TimerOp> {
        std::mem::take(&mut self.log)
    }

    /// Loads a persisted timer without recording an op.
    pub fn seed(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        self.timers
            .entry(rule_id)
            .or_default()
            .insert(path.to_owned(), at);
    }

    /// `rule_id`'s timers by path, in path order: the state a host persists.
    pub fn snapshot_for_rule(&self, rule_id: Uuid) -> impl Iterator<Item = (&str, DateTime<Utc>)> {
        self.timers
            .get(&rule_id)
            .into_iter()
            .flatten()
            .map(|(path, &at)| (path.as_str(), at))
    }
}

/// A missing child or `minutes <= 0` is false, with the child unevaluated and
/// the timer untouched. Otherwise the child evaluates first: false clears the
/// timer keyed by this node's path; the first true sets it and is false; later
/// trues hold once `minutes` fractional minutes have passed since it was set.
pub(crate) fn eval_sustained(p: &SustainedPayload, path: &str, env: &mut Env) -> bool {
    let Some(child) = &p.child else {
        return false;
    };
    if p.minutes <= 0 {
        return false;
    }

    let now = env.now;
    let child_result = eval_node(Some(child), &node_child_path(path, 0, Some(child)), env);

    if !child_result {
        env.timers.clear(env.rule_id, path);
        return false;
    }

    match env.timers.get_first_true(env.rule_id, path) {
        None => {
            env.timers.set_first_true(env.rule_id, path, now);
            false
        }
        Some(first) => env.held_for(first, p.minutes),
    }
}
