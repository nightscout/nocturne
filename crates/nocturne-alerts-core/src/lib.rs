//! # nocturne-alerts-core
//!
//! Nocturne's alert condition engine. The normative spec is
//! `docs/alerts/engine-semantics.md`; its machine-checkable form is the golden
//! corpus in `tests/Parity/AlertEngineCorpus/`, run by `tests/parity.rs`.
//!
//! Pure evaluation: no I/O, no clock (`now` is passed in), no persistence.
//! Timer and tracker state live in caller-owned stores
//! ([`sustained::TimerStore`], [`excursion::ExcursionTracker`]).

#![forbid(unsafe_code)]

/// The IANA time zone database release compiled into this crate, which
/// `time_of_day` and `day_of_week` resolve zones against.
pub const TZDB_VERSION: &str = chrono_tz::IANA_TZDB_VERSION;

pub mod classify;
mod compare;
pub mod context;
pub mod engine;
pub mod enums;
pub mod eval;
pub mod excursion;
mod leaf_identity;
pub mod model;
pub mod paths;
pub mod sustained;
pub mod validate;
pub mod wall_clock;
