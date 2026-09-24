//! UniFFI (Kotlin) surface, enabled by the `uniffi` feature.
//!
//! JSON-in/JSON-out `String` functions with the same envelopes as the C ABI,
//! so every consumer shares one wire contract. Each runs its C counterpart's
//! handler through the same panic guard: panics and unusable requests come
//! back as the error envelope, never as a foreign exception.

use crate::{envelope, envelope_string, tracker_envelope, validate_envelope};

/// `nocturne_alerts_evaluate`.
#[uniffi::export]
#[must_use]
pub(crate) fn evaluate(request_json: &str) -> String {
    envelope_string(|| envelope::evaluate(request_json))
}

/// `nocturne_alerts_evaluate_node`.
#[uniffi::export]
#[must_use]
pub(crate) fn evaluate_node(request_json: &str) -> String {
    envelope_string(|| envelope::evaluate_node(request_json))
}

/// `nocturne_alerts_classify`.
#[uniffi::export]
#[must_use]
pub(crate) fn classify(request_json: &str) -> String {
    envelope_string(|| envelope::classify_rule(request_json))
}

/// `nocturne_alerts_references_wall_clock`.
#[uniffi::export]
#[must_use]
pub(crate) fn references_wall_clock(request_json: &str) -> String {
    envelope_string(|| envelope::wall_clock(request_json))
}

/// `nocturne_alerts_leaf_paths`.
#[uniffi::export]
#[must_use]
pub(crate) fn leaf_paths(request_json: &str) -> String {
    envelope_string(|| envelope::leaf_paths(request_json))
}

/// `nocturne_alerts_describe`.
#[uniffi::export]
#[must_use]
pub(crate) fn describe(request_json: &str) -> String {
    envelope_string(|| envelope::describe(request_json))
}

/// `nocturne_alerts_validate`.
#[uniffi::export]
#[must_use]
pub(crate) fn validate(request_json: &str) -> String {
    envelope_string(|| validate_envelope::validate(request_json))
}

/// `nocturne_alerts_tracker_process`.
#[uniffi::export]
#[must_use]
pub(crate) fn tracker_process(request_json: &str) -> String {
    envelope_string(|| tracker_envelope::process(request_json))
}

/// `nocturne_alerts_tracker_force_close`.
#[uniffi::export]
#[must_use]
pub(crate) fn tracker_force_close(request_json: &str) -> String {
    envelope_string(|| tracker_envelope::force_close(request_json))
}

/// `nocturne_alerts_tracker_close_elapsed_hysteresis`.
#[uniffi::export]
#[must_use]
pub(crate) fn tracker_close_elapsed_hysteresis(request_json: &str) -> String {
    envelope_string(|| tracker_envelope::close_elapsed_hysteresis(request_json))
}

/// `nocturne_alerts_version`: a plain version string, not JSON.
#[uniffi::export]
#[must_use]
pub(crate) fn version() -> String {
    env!("CARGO_PKG_VERSION").to_owned()
}

/// `nocturne_alerts_tzdb_version`: a plain string, not JSON.
#[uniffi::export]
#[must_use]
pub(crate) fn tzdb_version() -> String {
    nocturne_alerts_core::TZDB_VERSION.to_owned()
}
