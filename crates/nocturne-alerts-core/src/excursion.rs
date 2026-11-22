//! The excursion state machine, idle → active → hysteresis
//! (engine-semantics.md §6). Hysteresis expires against the instant the
//! excursion entered it, persisted as `hysteresis_started_at`.

use std::collections::HashMap;

use chrono::{DateTime, TimeDelta, Utc};
use uuid::Uuid;

use crate::clock_anomaly::ClockAnchor;
use crate::eval::elapsed::{Elapsed, Measure};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum TrackerStateKind {
    Idle,
    Active,
    Hysteresis,
}

impl TrackerStateKind {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            TrackerStateKind::Idle => "idle",
            TrackerStateKind::Active => "active",
            TrackerStateKind::Hysteresis => "hysteresis",
        }
    }

    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "idle" => Some(TrackerStateKind::Idle),
            "active" => Some(TrackerStateKind::Active),
            "hysteresis" => Some(TrackerStateKind::Hysteresis),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum TransitionType {
    None,
    ExcursionOpened,
    ExcursionContinues,
    HysteresisStarted,
    HysteresisResumed,
    ExcursionClosed,
}

impl TransitionType {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            TransitionType::None => "none",
            TransitionType::ExcursionOpened => "opened",
            TransitionType::ExcursionContinues => "continues",
            TransitionType::HysteresisStarted => "hysteresis_started",
            TransitionType::HysteresisResumed => "hysteresis_resumed",
            TransitionType::ExcursionClosed => "closed",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum CloseReason {
    Hysteresis,
    AutoResolve,
    Manual,
    RuleDisabled,
}

impl CloseReason {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            CloseReason::Hysteresis => "hysteresis",
            CloseReason::AutoResolve => "auto",
            CloseReason::Manual => "manual",
            CloseReason::RuleDisabled => "rule-disabled",
        }
    }

    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "hysteresis" => Some(CloseReason::Hysteresis),
            "auto" => Some(CloseReason::AutoResolve),
            "manual" => Some(CloseReason::Manual),
            "rule-disabled" => Some(CloseReason::RuleDisabled),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[must_use]
pub struct Transition {
    pub kind: TransitionType,
    /// The excursion involved, as its rule's 1-based ordinal for it.
    pub excursion: Option<u32>,
    pub close_reason: Option<CloseReason>,
}

impl Transition {
    fn none() -> Self {
        Transition {
            kind: TransitionType::None,
            excursion: None,
            close_reason: None,
        }
    }
}

/// Per-rule persisted tracker state.
#[derive(Debug, Clone, Copy)]
pub struct TrackerState {
    pub state: TrackerStateKind,
    /// The active excursion's ordinal.
    pub active_excursion: Option<u32>,
    /// The ordinal of the excursion the rule opened last, 0 before its first.
    /// A rule numbers its excursions 1, 2, ... in the order it opens them, so
    /// an excursion is identified by its rule and this ordinal
    /// (engine-semantics.md §7).
    pub last_excursion: u32,
    pub updated_at: DateTime<Utc>,
    /// When the active excursion entered hysteresis; set only in
    /// [`TrackerStateKind::Hysteresis`].
    pub hysteresis_started_at: Option<DateTime<Utc>>,
    /// Idle after an auto-resolve closed an excursion whose condition still
    /// held: the rule opens nothing until an evaluation finds its condition or
    /// its auto-resolve tree false (engine-semantics.md §6.3).
    pub awaiting_rearm: bool,
}

impl TrackerState {
    /// What a stored tracker whose state string names no state is read as
    /// (engine-semantics.md §6): active while it holds an excursion, so that
    /// excursion goes on to close, otherwise idle, so the rule can fire.
    /// Hysteresis and re-arm start over.
    #[must_use]
    pub fn recovered(
        active_excursion: Option<u32>,
        last_excursion: u32,
        updated_at: DateTime<Utc>,
    ) -> Self {
        TrackerState {
            state: if active_excursion.is_some() {
                TrackerStateKind::Active
            } else {
                TrackerStateKind::Idle
            },
            active_excursion,
            last_excursion,
            updated_at,
            hysteresis_started_at: None,
            awaiting_rearm: false,
        }
    }
}

/// Rule inputs consumed by the tracker.
#[derive(Debug, Clone, Copy)]
pub struct TrackerRuleConfig {
    pub hysteresis_minutes: i32,
}

/// In-memory tracker for a set of rules. Each rule numbers its own
/// excursions ([`TrackerState::last_excursion`]).
#[derive(Debug, Default)]
pub struct ExcursionTracker {
    states: HashMap<Uuid, TrackerState>,
}

impl ExcursionTracker {
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    #[must_use]
    pub fn state(&self, rule_id: Uuid) -> Option<&TrackerState> {
        self.states.get(&rule_id)
    }

    /// Restores persisted per-rule state. State in hysteresis with no
    /// `hysteresis_started_at` adopts its `updated_at` as the start. A last
    /// ordinal below the active one is raised to it, so the next excursion
    /// never reuses the active one's ordinal.
    pub fn restore_state(&mut self, rule_id: Uuid, mut state: TrackerState) {
        if state.state == TrackerStateKind::Hysteresis && state.hysteresis_started_at.is_none() {
            state.hysteresis_started_at = Some(state.updated_at);
        }
        state.last_excursion = state
            .last_excursion
            .max(state.active_excursion.unwrap_or(0));
        self.states.insert(rule_id, state);
    }

    /// The excursion's ordinal while it is active or in hysteresis.
    #[must_use]
    pub fn active_excursion_id(&self, rule_id: Uuid) -> Option<u32> {
        let state = self.states.get(&rule_id)?;
        match state.state {
            TrackerStateKind::Active | TrackerStateKind::Hysteresis => state.active_excursion,
            _ => None,
        }
    }

    /// Whether the rule is idle awaiting re-arm (§6.3), so its evaluation
    /// reads the auto-resolve tree before the tracker.
    #[must_use]
    pub fn awaiting_rearm(&self, rule_id: Uuid) -> bool {
        self.states
            .get(&rule_id)
            .is_some_and(|s| s.state == TrackerStateKind::Idle && s.awaiting_rearm)
    }

    /// Advances the rule's state for one evaluation; `updated_at` becomes
    /// `now` whatever the transition. `auto_resolve_met` is the rule's
    /// auto-resolve tree this evaluation, read only while the rule awaits
    /// re-arm (§6.3); a rule without one passes false.
    pub fn process_evaluation(
        &mut self,
        rule_id: Uuid,
        config: TrackerRuleConfig,
        condition_met: bool,
        auto_resolve_met: bool,
        now: DateTime<Utc>,
    ) -> Transition {
        let mut state = *self.states.entry(rule_id).or_insert(TrackerState {
            state: TrackerStateKind::Idle,
            active_excursion: None,
            last_excursion: 0,
            updated_at: now,
            hysteresis_started_at: None,
            awaiting_rearm: false,
        });

        let transition = match state.state {
            TrackerStateKind::Idle => handle_idle(&mut state, condition_met, auto_resolve_met),
            TrackerStateKind::Active => handle_active(&mut state, condition_met, now),
            TrackerStateKind::Hysteresis => {
                handle_hysteresis(&mut state, config, condition_met, now)
            }
        };

        state.updated_at = now;
        self.states.insert(rule_id, state);
        transition
    }

    /// Closes the rule's excursion when it is in hysteresis and the window has
    /// elapsed (§6.1), without an evaluation: a host's periodic check for
    /// windows no evaluation arrives to close. Any other state is a `None`
    /// transition and unchanged.
    pub fn close_elapsed_hysteresis(
        &mut self,
        rule_id: Uuid,
        config: TrackerRuleConfig,
        now: DateTime<Utc>,
    ) -> Transition {
        let Some(state) = self.states.get_mut(&rule_id) else {
            return Transition::none();
        };
        if state.state != TrackerStateKind::Hysteresis
            || !hysteresis_elapsed(state, config.hysteresis_minutes, now)
        {
            return Transition::none();
        }
        state.updated_at = now;
        close_from_hysteresis(state)
    }

    /// Closes the rule's excursion, if it has one, from any state (§6.2);
    /// otherwise a `None` transition. An auto-resolve close of an active
    /// excursion leaves the rule awaiting re-arm (§6.3).
    pub fn force_close(
        &mut self,
        rule_id: Uuid,
        reason: CloseReason,
        now: DateTime<Utc>,
    ) -> Transition {
        let Some(state) = self.states.get_mut(&rule_id) else {
            return Transition::none();
        };
        let Some(excursion) = state.active_excursion else {
            return Transition::none();
        };
        state.awaiting_rearm =
            reason == CloseReason::AutoResolve && state.state == TrackerStateKind::Active;
        state.state = TrackerStateKind::Idle;
        state.active_excursion = None;
        state.hysteresis_started_at = None;
        state.updated_at = now;
        Transition {
            kind: TransitionType::ExcursionClosed,
            excursion: Some(excursion),
            close_reason: Some(reason),
        }
    }
}

fn handle_idle(
    state: &mut TrackerState,
    condition_met: bool,
    auto_resolve_met: bool,
) -> Transition {
    if state.awaiting_rearm {
        if condition_met && auto_resolve_met {
            return Transition::none();
        }
        state.awaiting_rearm = false;
    }
    if !condition_met {
        return Transition::none();
    }
    open_excursion(state)
}

fn open_excursion(state: &mut TrackerState) -> Transition {
    state.last_excursion = state.last_excursion.saturating_add(1);
    let ordinal = state.last_excursion;
    state.state = TrackerStateKind::Active;
    state.active_excursion = Some(ordinal);
    Transition {
        kind: TransitionType::ExcursionOpened,
        excursion: Some(ordinal),
        close_reason: None,
    }
}

fn handle_active(state: &mut TrackerState, condition_met: bool, now: DateTime<Utc>) -> Transition {
    if condition_met {
        return Transition {
            kind: TransitionType::ExcursionContinues,
            excursion: state.active_excursion,
            close_reason: None,
        };
    }
    state.state = TrackerStateKind::Hysteresis;
    state.hysteresis_started_at = Some(now);
    Transition {
        kind: TransitionType::HysteresisStarted,
        excursion: state.active_excursion,
        close_reason: None,
    }
}

fn handle_hysteresis(
    state: &mut TrackerState,
    config: TrackerRuleConfig,
    condition_met: bool,
    now: DateTime<Utc>,
) -> Transition {
    if condition_met {
        state.state = TrackerStateKind::Active;
        state.hysteresis_started_at = None;
        return Transition {
            kind: TransitionType::HysteresisResumed,
            excursion: state.active_excursion,
            close_reason: None,
        };
    }

    if hysteresis_elapsed(state, config.hysteresis_minutes, now) {
        return close_from_hysteresis(state);
    }
    Transition::none()
}

fn close_from_hysteresis(state: &mut TrackerState) -> Transition {
    let excursion = state.active_excursion;
    state.state = TrackerStateKind::Idle;
    state.active_excursion = None;
    state.hysteresis_started_at = None;
    state.awaiting_rearm = false;
    Transition {
        kind: TransitionType::ExcursionClosed,
        excursion,
        close_reason: Some(CloseReason::Hysteresis),
    }
}

/// `now - hysteresis_started_at >= hysteresis_minutes` (§6.1). Every
/// hysteresis state has a start: entering hysteresis sets one and
/// [`ExcursionTracker::restore_state`] adopts one. A start beyond the
/// tolerance ahead of `now` does not close the window (§5.4).
fn hysteresis_elapsed(state: &TrackerState, hysteresis_minutes: i32, now: DateTime<Utc>) -> bool {
    hysteresis_window(state, hysteresis_minutes).is_some_and(|w| w.truth(now).is_true())
}

/// The window that `state` started, as exact whole minutes since the start,
/// so a non-positive window has always elapsed; `None` without a start. One
/// whose end is past the representable calendar never elapses.
pub(crate) fn hysteresis_window(state: &TrackerState, hysteresis_minutes: i32) -> Option<Elapsed> {
    state.hysteresis_started_at.map(|started| Elapsed {
        anchor: started,
        source: Some(ClockAnchor::HysteresisStartedAt),
        measure: Measure::AtLeast(TimeDelta::minutes(i64::from(hysteresis_minutes))),
    })
}
