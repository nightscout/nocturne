//! UniFFI (Kotlin) surface, enabled by the `uniffi` feature.
//!
//! JSON-in/JSON-out `String` functions with the same envelopes as the C ABI,
//! so every consumer shares one wire contract. Each runs its C counterpart's
//! handler through the same panic guard: panics and unusable requests come
//! back as the error envelope, never as a foreign exception.

use crate::{envelope, envelope_string, validate_envelope};

/// `nocturne_alerts_evaluate`.
#[uniffi::export]
#[must_use]
pub fn evaluate(request_json: String) -> String {
    envelope_string(|| envelope::evaluate(&request_json))
}

/// `nocturne_alerts_evaluate_node`.
#[uniffi::export]
#[must_use]
pub fn evaluate_node(request_json: String) -> String {
    envelope_string(|| envelope::evaluate_node(&request_json))
}

/// `nocturne_alerts_leaf_paths`.
#[uniffi::export]
#[must_use]
pub fn leaf_paths(request_json: String) -> String {
    envelope_string(|| envelope::leaf_paths(&request_json))
}

/// `nocturne_alerts_describe`.
#[uniffi::export]
#[must_use]
pub fn describe(request_json: String) -> String {
    envelope_string(|| envelope::describe(&request_json))
}

/// `nocturne_alerts_validate`.
#[uniffi::export]
#[must_use]
pub fn validate(request_json: String) -> String {
    envelope_string(|| validate_envelope::validate(&request_json))
}

/// `nocturne_alerts_version`: a plain version string, not JSON.
#[uniffi::export]
#[must_use]
pub fn version() -> String {
    env!("CARGO_PKG_VERSION").to_owned()
}
