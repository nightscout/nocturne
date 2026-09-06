//! Sustained-condition timer state: an in-memory `IConditionTimerStore` that
//! records observable mutations (the state the host persists between
//! evaluations), plus the `sustained` container evaluation itself.

use std::collections::HashMap;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::compare::total_minutes;
use crate::eval::{Env, eval_node};
use crate::model::SustainedPayload;
use crate::paths::child_path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TimerOpKind {
    Set,
    Clear,
}

/// A recorded timer mutation. `at` is present only for `set`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TimerOp {
    pub kind: TimerOpKind,
    pub path: String,
    pub at: Option<DateTime<Utc>>,
}

/// `(rule_id, path) -> first_true_utc` store. Mirrors `RecordingTimerStore`:
/// a clear is recorded only when a timer actually existed (the engine calls
/// clear unconditionally on every child-false tick).
#[derive(Debug, Default)]
pub struct TimerStore {
    map: HashMap<(Uuid, String), DateTime<Utc>>,
    log: Vec<TimerOp>,
}

impl TimerStore {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn get_first_true(&self, rule_id: Uuid, path: &str) -> Option<DateTime<Utc>> {
        self.map.get(&(rule_id, path.to_string())).copied()
    }

    pub fn set_first_true(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        self.map.insert((rule_id, path.to_string()), at);
        self.log.push(TimerOp {
            kind: TimerOpKind::Set,
            path: path.to_string(),
            at: Some(at),
        });
    }

    pub fn clear(&mut self, rule_id: Uuid, path: &str) {
        if self.map.remove(&(rule_id, path.to_string())).is_some() {
            self.log.push(TimerOp {
                kind: TimerOpKind::Clear,
                path: path.to_string(),
                at: None,
            });
        }
    }

    pub fn clear_all_for_rule(&mut self, rule_id: Uuid) {
        let mut paths: Vec<String> = self
            .map
            .keys()
            .filter(|(r, _)| *r == rule_id)
            .map(|(_, p)| p.clone())
            .collect();
        paths.sort();
        for path in paths {
            self.map.remove(&(rule_id, path.clone()));
            self.log.push(TimerOp {
                kind: TimerOpKind::Clear,
                path,
                at: None,
            });
        }
    }

    /// Returns the ops recorded since the last drain, then clears the log.
    pub fn drain_ops(&mut self) -> Vec<TimerOp> {
        std::mem::take(&mut self.log)
    }

    /// Seeds a persisted timer without recording an op. Used by hosts that
    /// carry timer state across evaluations as data (e.g. the FFI envelope).
    pub fn seed(&mut self, rule_id: Uuid, path: &str, at: DateTime<Utc>) {
        self.map.insert((rule_id, path.to_string()), at);
    }

    /// Snapshot of every `(path, first_true)` entry for `rule_id`, sorted by
    /// path. The state a host persists between evaluations.
    pub fn snapshot_for_rule(&self, rule_id: Uuid) -> Vec<(String, DateTime<Utc>)> {
        let mut entries: Vec<(String, DateTime<Utc>)> = self
            .map
            .iter()
            .filter(|((r, _), _)| *r == rule_id)
            .map(|((_, p), at)| (p.clone(), *at))
            .collect();
        entries.sort_by(|a, b| a.0.cmp(&b.0));
        entries
    }
}

/// `SustainedEvaluator`: null child or `minutes <= 0` → false (child not
/// evaluated, timers untouched). Otherwise evaluate the child first
/// (path-extended `[0].{child.Type}`); child false clears the `(rule, path)`
/// timer; first true sets it and returns false (no 0-elapsed check on the set
/// tick); subsequent trues fire once `(now - first).TotalMinutes >= minutes`
/// in f64. The timer key path is the path of the sustained node itself.
pub(crate) fn eval_sustained(p: &SustainedPayload, path: &str, env: &mut Env) -> bool {
    let Some(child) = &p.child else {
        return false;
    };
    if p.minutes <= 0 {
        return false;
    }

    let now = env.now;
    let child_path = child_path(path, 0, child.type_str.as_deref());
    let child_result = eval_node(Some(child), &child_path, env);

    if !child_result {
        env.timers.clear(env.rule_id, path);
        return false;
    }

    match env.timers.get_first_true(env.rule_id, path) {
        None => {
            env.timers.set_first_true(env.rule_id, path, now);
            false
        }
        Some(first) => total_minutes(now - first) >= f64::from(p.minutes),
    }
}
