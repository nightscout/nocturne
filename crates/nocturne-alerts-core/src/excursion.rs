//! `ExcursionTracker` state machine: idle → confirming → active → hysteresis.
//!
//! Reproduces the C# tracker exactly, including the sliding hysteresis-expiry
//! proxy: the per-evaluation expiry check uses the **previous** evaluation's
//! `UpdatedAt` (`now >= prev_updated_at + HysteresisMinutes`), and `UpdatedAt`
//! is rewritten to `now` after **every** evaluation. The production sweep that
//! force-closes all hysteresis windows is host-side and out of scope.

use std::collections::HashMap;

use chrono::{DateTime, TimeDelta, Utc};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TrackerStateKind {
    Idle,
    Confirming,
    Active,
    Hysteresis,
}

impl TrackerStateKind {
    pub fn wire(self) -> &'static str {
        match self {
            TrackerStateKind::Idle => "idle",
            TrackerStateKind::Confirming => "confirming",
            TrackerStateKind::Active => "active",
            TrackerStateKind::Hysteresis => "hysteresis",
        }
    }

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
pub enum TransitionType {
    None,
    ExcursionOpened,
    ExcursionContinues,
    HysteresisStarted,
    HysteresisResumed,
    ExcursionClosed,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CloseReason {
    Hysteresis,
    AutoResolve,
    Manual,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
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
    pub fn new() -> Self {
        Self::default()
    }

    pub fn state(&self, rule_id: Uuid) -> Option<&TrackerState> {
        self.states.get(&rule_id)
    }

    /// Restores persisted per-rule state. Used by hosts that carry tracker
    /// state across evaluations as data (e.g. the FFI envelope).
    pub fn restore_state(&mut self, rule_id: Uuid, state: TrackerState) {
        self.states.insert(rule_id, state);
    }

    /// The 1-based ordinal the next opened excursion will receive.
    pub fn next_excursion_ordinal(&self) -> u32 {
        self.next_ordinal + 1
    }

    /// Sets the 1-based ordinal the next opened excursion will receive
    /// (values below 1 are clamped to 1).
    pub fn set_next_excursion_ordinal(&mut self, next: u32) {
        self.next_ordinal = next.saturating_sub(1);
    }

    /// `GetActiveExcursionIdAsync`: returns the id only in active/hysteresis.
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
        });

        let transition = match state.state {
            TrackerStateKind::Idle => self.handle_idle(&mut state, config, condition_met),
            TrackerStateKind::Confirming => {
                self.handle_confirming(&mut state, config, condition_met)
            }
            TrackerStateKind::Active => handle_active(&mut state, condition_met),
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
        state.confirmation_count += 1;
        if state.confirmation_count >= config.confirmation_readings {
            return self.open_excursion(state);
        }
        Transition::none()
    }

    fn open_excursion(&mut self, state: &mut TrackerState) -> Transition {
        self.next_ordinal += 1;
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
        state.updated_at = now;
        Transition {
            kind: TransitionType::ExcursionClosed,
            excursion: Some(excursion),
            close_reason: Some(reason),
        }
    }
}

fn handle_active(state: &mut TrackerState, condition_met: bool) -> Transition {
    if condition_met {
        return Transition {
            kind: TransitionType::ExcursionContinues,
            excursion: state.active_excursion,
            close_reason: None,
        };
    }
    state.state = TrackerStateKind::Hysteresis;
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
        return Transition {
            kind: TransitionType::HysteresisResumed,
            excursion: state.active_excursion,
            close_reason: None,
        };
    }

    // Sliding proxy [anomaly — normative]: the previous evaluation's
    // UpdatedAt stands in for "hysteresis started"; DateTime.AddMinutes of an
    // int minute count is an exact millisecond addition.
    let hysteresis_expiry =
        state.updated_at + TimeDelta::milliseconds(i64::from(config.hysteresis_minutes) * 60_000);
    if now >= hysteresis_expiry {
        let excursion = state.active_excursion;
        state.state = TrackerStateKind::Idle;
        state.confirmation_count = 0;
        state.active_excursion = None;
        return Transition {
            kind: TransitionType::ExcursionClosed,
            excursion,
            close_reason: Some(CloseReason::Hysteresis),
        };
    }
    Transition::none()
}
