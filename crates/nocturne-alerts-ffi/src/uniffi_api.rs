//! UniFFI (Kotlin) surface, enabled by the `uniffi` cargo feature.
//!
//! Deliberately JSON-in/JSON-out `String` functions — the same envelope
//! contract as the C ABI, documented in the crate `README.md` — so there is
//! exactly one wire contract for every consumer and no parallel typed surface
//! that could drift. Each function delegates to the same internal envelope
//! handler as its C counterpart, through the same panic guard: panics and
//! unusable requests come back as `{"schema_version":1,"ok":false,"error":…}`,
//! never as a foreign exception.

use crate::{envelope, envelope_string};

/// Evaluates one rule for one tick (Kotlin counterpart of
/// `nocturne_alerts_evaluate`). `request_json` and the returned string are the
/// request/response envelopes documented in `README.md`.
#[uniffi::export]
pub fn evaluate(request_json: String) -> String {
    envelope_string(|| envelope::evaluate(&request_json))
}

/// Evaluates one condition node for one instant outside the per-rule driver
/// (Kotlin counterpart of `nocturne_alerts_evaluate_node`).
#[uniffi::export]
pub fn evaluate_node(request_json: String) -> String {
    envelope_string(|| envelope::evaluate_node_envelope(&request_json))
}

/// Enumerates the canonical condition paths and leaf ids of a condition tree
/// (Kotlin counterpart of `nocturne_alerts_leaf_paths`).
#[uniffi::export]
pub fn leaf_paths(request_json: String) -> String {
    envelope_string(|| envelope::leaf_paths(&request_json))
}

/// Decodes a rule's condition tree into a structured, leaf-id-tagged
/// description for condition readouts (Kotlin counterpart of
/// `nocturne_alerts_describe`).
#[uniffi::export]
pub fn describe(request_json: String) -> String {
    envelope_string(|| envelope::describe(&request_json))
}

/// Returns the crate version as a plain (non-JSON) string, exactly like
/// `nocturne_alerts_version`.
#[uniffi::export]
pub fn version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}
