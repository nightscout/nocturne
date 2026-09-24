//! # nocturne-alerts-ffi
//!
//! C ABI wrapper around [`nocturne_alerts_core`]: JSON in, JSON out, UTF-8,
//! NUL-terminated. The envelopes are documented in this crate's `README.md`
//! and are the contract for the .NET bindings and, behind the `uniffi`
//! feature, the Kotlin bindings (`uniffi_api`).
//!
//! Every entry point catches panics and returns an error envelope instead of
//! unwinding across the boundary, rejects null pointers and invalid UTF-8 the
//! same way, and returns a pointer the caller releases with
//! [`nocturne_alerts_free_string`] exactly once.

#[cfg(panic = "abort")]
compile_error!("the FFI boundary catches panics, which needs panic = \"unwind\"");

mod c_abi;
mod envelope;
mod validate_envelope;

#[cfg(feature = "uniffi")]
mod uniffi_api;

#[cfg(feature = "uniffi")]
uniffi::setup_scaffolding!("nocturne_alerts");

#[cfg(test)]
mod tests;

#[cfg(test)]
mod validate_tests;

use std::any::Any;
use std::panic::{AssertUnwindSafe, catch_unwind};

use serde_json::{Value, json};

pub use c_abi::*;
use envelope::SCHEMA_VERSION;

fn error_json(message: &str) -> String {
    json!({ "schema_version": SCHEMA_VERSION, "ok": false, "error": message }).to_string()
}

fn panic_message(payload: &(dyn Any + Send)) -> &str {
    payload
        .downcast_ref::<&str>()
        .copied()
        .or_else(|| payload.downcast_ref::<String>().map(String::as_str))
        .unwrap_or("unknown panic payload")
}

/// Runs `f` inside a panic guard and renders the result as an envelope:
/// `Ok` as is, `Err` and panics as the error envelope. Shared by the C ABI and
/// the UniFFI surface.
fn envelope_string(f: impl FnOnce() -> Result<Value, String>) -> String {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(value)) => value.to_string(),
        Ok(Err(message)) => error_json(&message),
        Err(payload) => error_json(&format!(
            "panic in alert engine: {}",
            panic_message(payload.as_ref())
        )),
    }
}
