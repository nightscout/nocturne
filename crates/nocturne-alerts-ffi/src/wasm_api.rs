//! Browser surface, enabled by the `wasm` feature: the docs portal runs the
//! real engine against fixture traces rather than a copy of its semantics.
//!
//! Same envelopes as the C ABI, through the same handlers, so the browser is
//! one more consumer of the one wire contract. Only the entry points the
//! simulator calls are exported.

use wasm_bindgen::prelude::wasm_bindgen;

use crate::{envelope_string, replay_envelope, validate_envelope};

/// `nocturne_alerts_replay`.
#[wasm_bindgen]
#[must_use]
pub fn replay(request_json: &str) -> String {
    envelope_string(|| replay_envelope::replay(request_json))
}

/// `nocturne_alerts_validate`.
#[wasm_bindgen]
#[must_use]
pub fn validate(request_json: &str) -> String {
    envelope_string(|| validate_envelope::validate(request_json))
}

/// `nocturne_alerts_version`: a plain version string, not JSON.
#[wasm_bindgen]
#[must_use]
pub fn version() -> String {
    env!("CARGO_PKG_VERSION").to_owned()
}
