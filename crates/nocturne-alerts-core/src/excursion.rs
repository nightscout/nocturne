//! `ExcursionTracker` state machine: idle → confirming → active → hysteresis
//! (`docs/alerts/engine-semantics.md` §6). Hysteresis expires against the
//! instant the excursion entered hysteresis, persisted as
//! `hysteresis_started_at`.

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
    /// Active excursion as a stable 1-based ordinal (creation order within the
    /// tracker's lifetime), engine-independent like the corpus snapshots.
    pub active_excursion: Option<u32>,
    pub updated_at: DateTime<Utc>,
    /// When the active excursion entered hysteresis; set only in
    /// [`TrackerStateKind::Hysteresis`].
    pub hysteresis_started_at: Option<DateTime<Utc>>,
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

    /// Restores persisted per-rule state. Used by hosts that carry tracker
    /// state across evaluations as data (e.g. the FFI envelope).
    ///
    /// State persisted before `hysteresis_started_at` existed carries none
    /// while in hysteresis; its `updated_at` is adopted once as the start.
    pub fn restore_state(&mut self, rule_id: Uuid, mut state: TrackerState) {
        if state.state == TrackerStateKind::Hysteresis && state.hysteresis_started_at.is_none() {
            state.hysteresis_started_at = Some(state.updated_at);
        }
        self.states.insert(rule_id, state);
    }

    /// The 1-based ordinal the next opened excursion will receive.
    #[must_use]
    pub fn next_excursion_ordinal(&self) -> u32 {
        self.next_ordinal + 1
    }

    /// Sets the 1-based ordinal the next opened excursion will receive
    /// (values below 1 are clamped to 1).
    pub fn set_next_excursion_ordinal(&mut self, next: u32) {
        self.next_ordinal = next.saturating_sub(1);
    }

    /// `GetActiveExcursionIdAsync`: returns the id only in active/hysteresis.
    #[must_use]
    pub fn active_excursion_id(&self, rule_id: Uuid) -> Option<u32> {
        let state = self.states.get(&rule_id)?;
        match state.state {
            TrackerStateKind::Active | TrackerStateKind::Hysteresis => state.active_excursion,
            _ => None,
        }
    }

    /// One evaluation = one call. `state.updated_at = now` is persisted after
    /// every call regardless of transition.
    pub fn process_evaluation(
        &mut self,
        rule_id: Uuid,
        config: TrackerRuleConfig,
        condition_met: bool,
        now: DateTime<Utc>,
    ) -> Transition {
        let mut state = *self.states.entry(rule_id).or_insert(TrackerState {
            state: TrackerStateKind::Idle,
            confirmation_count: 0,
            active_excursion: None,
            updated_at: now,
            hysteresis_started_at: None,
        });

        let transition = match state.state {
            TrackerStateKind::Idle => self.handle_idle(&mut state, config, condition_met),
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
    ) -> Transition {
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

    /// `ForceCloseAsync`: closes from any state with an `ActiveExcursionId`;
    /// otherwise a no-op `None` transition.
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

    let started = state.hysteresis_started_at.unwrap_or(state.updated_at);
    if hysteresis_elapsed(started, config.hysteresis_minutes, now) {
        let excursion = state.active_excursion;
        state.state = TrackerStateKind::Idle;
        state.confirmation_count = 0;
        state.active_excursion = None;
        state.hysteresis_started_at = None;
        return Transition {
            kind: TransitionType::ExcursionClosed,
            excursion,
            close_reason: Some(CloseReason::Hysteresis),
        };
    }
    Transition::none()
}

/// `now - started >= hysteresis_minutes` as exact whole minutes, so a
/// non-positive window has always elapsed and an expiry past the representable
/// calendar never arrives (§6.1).
fn hysteresis_elapsed(started: DateTime<Utc>, hysteresis_minutes: i32, now: DateTime<Utc>) -> bool {
    started
        .checked_add_signed(TimeDelta::minutes(i64::from(hysteresis_minutes)))
        .is_some_and(|expiry| now >= expiry)
}
