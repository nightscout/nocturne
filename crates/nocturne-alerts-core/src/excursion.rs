//! The excursion state machine, idle → confirming → active → hysteresis
//! (engine-semantics.md §6). Hysteresis expires against the instant the
//! excursion entered it, persisted as `hysteresis_started_at`.

use std::collections::HashMap;

use chrono::{DateTime, TimeDelta, Utc};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[non_exhaustive]
pub enum TrackerStateKind {
    Idle,
    Confirming,
    Active,
    Hysteresis,
}

impl TrackerStateKind {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            TrackerStateKind::Idle => "idle",
            TrackerStateKind::Confirming => "confirming",
            TrackerStateKind::Active => "active",
            TrackerStateKind::Hysteresis => "hysteresis",
        }
    }

    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "idle" => Some(TrackerStateKind::Idle),
            "confirming" => Some(TrackerStateKind::Confirming),
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
}

impl CloseReason {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            CloseReason::Hysteresis => "hysteresis",
            CloseReason::AutoResolve => "auto",
            CloseReason::Manual => "manual",
        }
    }

    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "hysteresis" => Some(CloseReason::Hysteresis),
            "auto" => Some(CloseReason::AutoResolve),
            "manual" => Some(CloseReason::Manual),
            _ => None,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[must_use]
pub struct Transition {
    pub kind: TransitionType,
    /// The excursion involved, as its 1-based creation ordinal.
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
    pub confirmation_count: i32,
    /// The active excursion's 1-based creation ordinal.
    pub active_excursion: Option<u32>,
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
    /// Confirmation, hysteresis and re-arm start over.
    #[must_use]
    pub fn recovered(active_excursion: Option<u32>, updated_at: DateTime<Utc>) -> Self {
        TrackerState {
            state: if active_excursion.is_some() {
                TrackerStateKind::Active
            } else {
                TrackerStateKind::Idle
            },
            confirmation_count: 0,
            active_excursion,
            updated_at,
            hysteresis_started_at: None,
            awaiting_rearm: false,
        }
    }
}

/// Rule inputs consumed by the tracker.
#[derive(Debug, Clone, Copy)]
pub struct TrackerRuleConfig {
    pub confirmation_readings: i32,
    pub hysteresis_minutes: i32,
}

/// In-memory tracker for a set of rules. Excursion ids are 1-based ordinals in
/// creation order.
#[derive(Debug, Default)]
pub struct ExcursionTracker {
    states: HashMap<Uuid, TrackerState>,
    next_ordinal: u32,
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
    /// `hysteresis_started_at` adopts its `updated_at` as the start.
    pub fn restore_state(&mut self, rule_id: Uuid, mut state: TrackerState) {
        if state.state == TrackerStateKind::Hysteresis && state.hysteresis_started_at.is_none() {
            state.hysteresis_started_at = Some(state.updated_at);
        }
        self.states.insert(rule_id, state);
    }

    /// The 1-based ordinal the next opened excursion will receive.
    #[must_use]
    pub fn next_excursion_ordinal(&self) -> u32 {
        self.next_ordinal.saturating_add(1)
    }

    /// Sets the 1-based ordinal the next opened excursion will receive
    /// (values below 1 are clamped to 1).
    pub fn set_next_excursion_ordinal(&mut self, next: u32) {
        self.next_ordinal = next.saturating_sub(1);
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
            confirmation_count: 0,
            active_excursion: None,
            updated_at: now,
            hysteresis_started_at: None,
            awaiting_rearm: false,
        });

        let transition = match state.state {
            TrackerStateKind::Idle => {
                self.handle_idle(&mut state, config, condition_met, auto_resolve_met)
            }
            TrackerStateKind::Confirming => {
                self.handle_confirming(&mut state, config, condition_met)
            }
            TrackerStateKind::Active => handle_active(&mut state, condition_met, now),
            TrackerStateKind::Hysteresis => {
                handle_hysteresis(&mut state, config, condition_met, now)
            }
        };

        state.updated_at = now;
        self.states.insert(rule_id, state);
        transition
    }

    fn handle_idle(
        &mut self,
        state: &mut TrackerState,
        config: TrackerRuleConfig,
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
        if config.confirmation_readings <= 1 {
            return self.open_excursion(state);
        }
        state.state = TrackerStateKind::Confirming;
        state.confirmation_count = 1;
        Transition::none()
    }

    fn handle_confirming(
        &mut self,
        state: &mut TrackerState,
        config: TrackerRuleConfig,
        condition_met: bool,
    ) -> Transition {
        if !condition_met {
            state.state = TrackerStateKind::Idle;
            state.confirmation_count = 0;
            return Transition::none();
        }
        state.confirmation_count = state.confirmation_count.saturating_add(1);
        if state.confirmation_count >= config.confirmation_readings {
            return self.open_excursion(state);
        }
        Transition::none()
    }

    fn open_excursion(&mut self, state: &mut TrackerState) -> Transition {
        self.next_ordinal = self.next_ordinal.saturating_add(1);
        let ordinal = self.next_ordinal;
        state.state = TrackerStateKind::Active;
        state.confirmation_count = 0;
        state.active_excursion = Some(ordinal);
        Transition {
            kind: TransitionType::ExcursionOpened,
            excursion: Some(ordinal),
            close_reason: None,
        }
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
        state.confirmation_count = 0;
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
    state.confirmation_count = 0;
    state.active_excursion = None;
    state.hysteresis_started_at = None;
    state.awaiting_rearm = false;
    Transition {
        kind: TransitionType::ExcursionClosed,
        excursion,
        close_reason: Some(CloseReason::Hysteresis),
    }
}

/// `now - hysteresis_started_at >= hysteresis_minutes` as exact whole
/// minutes, so a non-positive window has always elapsed and an expiry past the
/// representable calendar never arrives (§6.1). Every hysteresis state has a
/// start: entering hysteresis sets one and [`ExcursionTracker::restore_state`]
/// adopts one.
fn hysteresis_elapsed(state: &TrackerState, hysteresis_minutes: i32, now: DateTime<Utc>) -> bool {
    state
        .hysteresis_started_at
        .and_then(|started| {
            started.checked_add_signed(TimeDelta::minutes(i64::from(hysteresis_minutes)))
        })
        .is_some_and(|expiry| now >= expiry)
}
