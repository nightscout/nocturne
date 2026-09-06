//! # nocturne-alerts-core
//!
//! Pure-Rust port of the Nocturne leaf/node alert evaluation engine,
//! behaviourally exact against the C# implementation. The normative spec is
//! `docs/alerts/engine-semantics.md`; the machine-checkable form is the golden
//! corpus in `tests/Parity/AlertEngineCorpus/`, exercised by `tests/parity.rs`.
//!
//! Boundary: pure evaluation only. No I/O, no clock (`now` is passed in), no
//! persistence — timer and tracker state live in caller-owned in-memory
//! stores ([`sustained::TimerStore`], [`excursion::ExcursionTracker`]).

pub mod classify;
pub mod compare;
pub mod context;
pub mod engine;
pub mod eval;
pub mod excursion;
pub mod leaf_identity;
pub mod model;
pub mod paths;
pub mod sustained;
