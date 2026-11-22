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
    /// A hysteresis that the hold bound began closed (engine-semantics.md
    /// §6.4): the root had been unknown for [`HOLD_BOUND`].
    HoldBound,
}

impl CloseReason {
    #[must_use]
    pub fn wire(self) -> &'static str {
        match self {
            CloseReason::Hysteresis => "hysteresis",
            CloseReason::AutoResolve => "auto",
            CloseReason::Manual => "manual",
            CloseReason::RuleDisabled => "rule-disabled",
            CloseReason::HoldBound => "hold-bound",
        }
    }

    #[must_use]
    pub fn from_wire(s: &str) -> Option<Self> {
        match s {
            "hysteresis" => Some(CloseReason::Hysteresis),
            "auto" => Some(CloseReason::AutoResolve),
            "manual" => Some(CloseReason::Manual),
            "rule-disabled" => Some(CloseReason::RuleDisabled),
            "hold-bound" => Some(CloseReason::HoldBound),
            _ => None,
        }
    }
}

/// How long a rule may hold (engine-semantics.md §6.4) before its hold is
/// reported, and an excursion it holds open reads its root as not met.
pub const HOLD_BOUND: TimeDelta = TimeDelta::hours(24);

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
    /// When the current hold began (engine-semantics.md §6.4): the first of
    /// an unbroken run of held evaluations. Cleared by any evaluation whose
    /// root is known or read as known.
    pub held_since: Option<DateTime<Utc>>,
}

impl TrackerState {
    /// What a rule never evaluated before holds after its first held
    /// evaluation, at `now`.
    fn held_from(now: DateTime<Utc>) -> Self {
        TrackerState {
            held_since: Some(now),
            ..Self::fresh(now)
        }
    }

    fn fresh(now: DateTime<Utc>) -> Self {
        TrackerState {
            state: TrackerStateKind::Idle,
            active_excursion: None,
            last_excursion: 0,
            updated_at: now,
            hysteresis_started_at: None,
            awaiting_rearm: false,
            held_since: None,
        }
    }

    /// Whether the state records nothing but a hold, so every evaluation of
    /// the rule may have been held: idle, no excursion, not awaiting re-arm.
    #[must_use]
    pub fn holds_only_a_hold(&self) -> bool {
        self.held_since.is_some()
            && self.state == TrackerStateKind::Idle
            && self.active_excursion.is_none()
            && self.last_excursion == 0
            && !self.awaiting_rearm
    }

    /// The hold's bound as an elapsed time since it began; `None` when the
    /// rule is not held.
    pub(crate) fn hold_bound(&self) -> Option<Elapsed> {
        self.held_since.map(|since| Elapsed {
            anchor: since,
            source: Some(ClockAnchor::HeldSince),
            measure: Measure::AtLeast(HOLD_BOUND),
        })
    }

    /// A stored hold this engine cannot have written (engine-semantics.md
    /// §6.4): while held, `updated_at` stays at or before `held_since` until
    /// the evaluation that reaches the bound sets it past the bound, and from
    /// then on the state is never active, since only a known root, which ends
    /// the hold, makes it active again. A later `updated_at` before the bound,
    /// or an active state past it, was written by an engine that does not
    /// record holds, after the hold began, so the hold is not continuous.
    fn hold_is_stale(&self) -> bool {
        let Some(since) = self.held_since else {
            return false;
        };
        let Some(bound) = since.checked_add_signed(HOLD_BOUND) else {
            return false;
        };
        (self.updated_at > since && self.updated_at < bound)
            || (self.updated_at >= bound && self.state == TrackerStateKind::Active)
    }

    /// Held, and the evaluation that reaches the bound is still to come.
    #[must_use]
    pub fn hold_pending(&self) -> bool {
        self.held_since.is_some() && !self.hold_bound_reported()
    }

    /// Where a pending hold reaches its bound.
    #[must_use]
    pub fn hold_bound_at(&self) -> Option<DateTime<Utc>> {
        self.held_since
            .filter(|_| self.hold_pending())
            .and_then(|since| since.checked_add_signed(HOLD_BOUND))
    }

    /// Whether the evaluation that reached the hold's bound was made: it sets
    /// `updated_at`, which no held evaluation before the bound moves.
    pub(crate) fn hold_bound_reported(&self) -> bool {
        self.held_since
            .and_then(|since| since.checked_add_signed(HOLD_BOUND))
            .is_some_and(|bound| self.updated_at >= bound)
    }

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
            held_since: None,
        }
    }

    /// `updated_at = now`, except while held: then only the evaluation that
    /// reaches the bound moves it ([`TrackerState::hold_bound_reported`]).
    fn touch(&mut self, now: DateTime<Utc>) {
        if self.held_since.is_none() {
            self.updated_at = now;
        }
    }
}

/// What a held evaluation did (engine-semantics.md §6.4).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[must_use]
pub struct HeldOutcome {
    pub transition: Transition,
    /// This evaluation reached the hold's bound: once per hold.
    pub held_too_long: bool,
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

    /// Whether the rule's excursion is open (engine-semantics.md §3, clear
    /// margin): it has one that is active or in hysteresis.
    #[must_use]
    pub fn excursion_open(&self, rule_id: Uuid) -> bool {
        self.active_excursion_id(rule_id).is_some()
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
    /// The root is known, or read as known: the hold, if any, is over.
    pub fn process_evaluation(
        &mut self,
        rule_id: Uuid,
        config: TrackerRuleConfig,
        condition_met: bool,
        auto_resolve_met: bool,
        now: DateTime<Utc>,
    ) -> Transition {
        let state = self
            .states
            .entry(rule_id)
            .or_insert_with(|| TrackerState::fresh(now));
        state.held_since = None;
        advance(state, config, condition_met, auto_resolve_met, now)
    }

    /// An evaluation the rule holds (§6.4). The first of a hold records its
    /// start, creating the rule's state if it has none. Before the bound only
    /// an elapsed hysteresis window closes. The evaluation that reaches it is
    /// reported, once per hold, and from it an open excursion reads its root as
    /// not met, so it goes through hysteresis and closes; a rule with none is
    /// left as it is. The bound's start beyond the tolerance ahead of `now`
    /// is not reached on that evaluation (§5.4).
    pub fn process_held(
        &mut self,
        rule_id: Uuid,
        config: TrackerRuleConfig,
        now: DateTime<Utc>,
    ) -> HeldOutcome {
        let state = self
            .states
            .entry(rule_id)
            .or_insert_with(|| TrackerState::held_from(now));
        if state.hold_is_stale() {
            state.held_since = None;
        }
        state.held_since.get_or_insert(now);
        let reached = state
            .hold_bound()
            .is_some_and(|bound| bound.truth(now).is_true());
        if !reached {
            return HeldOutcome {
                transition: self.close_elapsed_hysteresis(rule_id, config, now),
                held_too_long: false,
            };
        }
        let held_too_long = !state.hold_bound_reported();
        let transition = if state.active_excursion.is_some() {
            advance(state, config, false, false, now)
        } else {
            if held_too_long {
                state.updated_at = now;
            }
            Transition::none()
        };
        HeldOutcome {
            transition,
            held_too_long,
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
        state.touch(now);
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
        state.touch(now);
        Transition {
            kind: TransitionType::ExcursionClosed,
            excursion: Some(excursion),
            close_reason: Some(reason),
        }
    }
}

/// One evaluation's step of the state machine; `updated_at` becomes `now`
/// whatever the transition.
fn advance(
    state: &mut TrackerState,
    config: TrackerRuleConfig,
    condition_met: bool,
    auto_resolve_met: bool,
    now: DateTime<Utc>,
) -> Transition {
    let transition = match state.state {
        TrackerStateKind::Idle => handle_idle(state, condition_met, auto_resolve_met),
        TrackerStateKind::Active => handle_active(state, condition_met, now),
        TrackerStateKind::Hysteresis => handle_hysteresis(state, config, condition_met, now),
    };
    state.updated_at = now;
    transition
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

/// A hysteresis that began once the hold had lasted its bound was begun by
/// the bound, since a known root ends the hold.
fn close_from_hysteresis(state: &mut TrackerState) -> Transition {
    let excursion = state.active_excursion;
    let bound_began = state
        .held_since
        .filter(|_| !state.hold_is_stale())
        .and_then(|held| held.checked_add_signed(HOLD_BOUND))
        .zip(state.hysteresis_started_at)
        .is_some_and(|(bound, started)| started >= bound);
    let reason = if bound_began {
        CloseReason::HoldBound
    } else {
        CloseReason::Hysteresis
    };
    state.state = TrackerStateKind::Idle;
    state.active_excursion = None;
    state.hysteresis_started_at = None;
    state.awaiting_rearm = false;
    Transition {
        kind: TransitionType::ExcursionClosed,
        excursion,
        close_reason: Some(reason),
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
