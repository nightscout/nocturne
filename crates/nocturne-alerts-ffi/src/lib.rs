//! # nocturne-alerts-ffi
//!
//! C ABI wrapper around [`nocturne_alerts_core`]: JSON-in/JSON-out, UTF-8,
//! NUL-terminated. The envelope schema is documented in this crate's
//! `README.md` and is the contract consumed by the .NET bindings
//! (`Nocturne.Core.Alerts.Native`) and, behind the `uniffi` cargo feature,
//! the UniFFI-generated Kotlin bindings ([`uniffi_api`]).
//!
//! Boundary guarantees:
//! - every entrypoint catches panics and returns an error envelope
//!   (`{"schema_version":1,"ok":false,"error":"…"}`) instead of unwinding
//!   across the FFI boundary;
//! - null pointers and invalid UTF-8 are rejected with error envelopes;
//! - every returned pointer is owned by the caller and must be released with
//!   [`nocturne_alerts_free_string`] exactly once.

mod envelope;

#[cfg(feature = "uniffi")]
mod uniffi_api;

#[cfg(feature = "uniffi")]
uniffi::setup_scaffolding!("nocturne_alerts");

#[cfg(test)]
mod tests;

use std::any::Any;
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

use envelope::SCHEMA_VERSION;

/// Builds the `ok: false` error envelope.
fn error_json(message: &str) -> String {
    serde_json::to_string(&serde_json::json!({
        "schema_version": SCHEMA_VERSION,
        "ok": false,
        "error": message,
    }))
    .unwrap_or_else(|_| {
        r#"{"schema_version":1,"ok":false,"error":"error envelope serialization failed"}"#
            .to_string()
    })
}

/// Transfers a Rust string to the caller as a heap-allocated, NUL-terminated
/// C string. serde_json escapes control characters, so an interior NUL can
/// only come from a non-JSON string (e.g. the version); fail safe regardless.
fn into_c_string(s: String) -> *mut c_char {
    match CString::new(s) {
        Ok(cs) => cs.into_raw(),
        Err(_) => CString::new(error_json("response contained an interior NUL byte"))
            .expect("static error envelope has no NUL")
            .into_raw(),
    }
}

fn panic_message(payload: &Box<dyn Any + Send>) -> &str {
    if let Some(s) = payload.downcast_ref::<&str>() {
        s
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s
    } else {
        "unknown panic payload"
    }
}

/// Runs `f` inside a panic guard and renders the outcome as a JSON envelope
/// string: `Ok` values are serialized, `Err`s and panics become the
/// `ok: false` error envelope. Shared by the C ABI and the UniFFI surface so
/// both expose identical envelope semantics.
fn envelope_string(f: impl FnOnce() -> Result<serde_json::Value, String>) -> String {
    match catch_unwind(AssertUnwindSafe(f)) {
        Ok(Ok(value)) => serde_json::to_string(&value)
            .unwrap_or_else(|e| error_json(&format!("response serialization failed: {e}"))),
        Ok(Err(message)) => error_json(&message),
        Err(payload) => error_json(&format!(
            "panic in alert engine: {}",
            panic_message(&payload)
        )),
    }
}

/// Runs `f` at the FFI boundary: panics and `Err`s become error envelopes,
/// `Ok` values are serialized. Always returns a caller-owned C string.
fn boundary(f: impl FnOnce() -> Result<serde_json::Value, String>) -> *mut c_char {
    into_c_string(envelope_string(f))
}

/// Reads a caller-supplied C string as UTF-8.
///
/// # Safety
/// `ptr` must be null or point to a NUL-terminated string valid for reads for
/// the duration of the call.
unsafe fn read_utf8<'a>(ptr: *const c_char, what: &str) -> Result<&'a str, String> {
    if ptr.is_null() {
        return Err(format!("{what} pointer is null"));
    }
    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map_err(|_| format!("{what} is not valid UTF-8"))
}

/// Returns the crate version as a plain (non-JSON) string.
/// Free with [`nocturne_alerts_free_string`].
#[unsafe(no_mangle)]
pub extern "C" fn nocturne_alerts_version() -> *mut c_char {
    into_c_string(env!("CARGO_PKG_VERSION").to_string())
}

/// Evaluates one rule for one tick. `request_json` / the returned string are
/// the request/response envelopes documented in `README.md`.
/// Free the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_evaluate(request_json: *const c_char) -> *mut c_char {
    boundary(|| {
        let input = unsafe { read_utf8(request_json, "request") }?;
        envelope::evaluate(input)
    })
}

/// Evaluates one condition node for one instant outside the per-rule driver
/// (no tracker, no auto-resolve) — the auxiliary-scope counterpart of
/// [`nocturne_alerts_evaluate`] used for smart-snooze conditions and the
/// sweep's periodic auto-resolve. Request/response envelopes are documented in
/// `README.md`. Free the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_evaluate_node(request_json: *const c_char) -> *mut c_char {
    boundary(|| {
        let input = unsafe { read_utf8(request_json, "request") }?;
        envelope::evaluate_node_envelope(input)
    })
}

/// Derives a rule's scope class (`low | high | composite | undirected`) for
/// scoped Do Not Disturb from its `condition_type` + `condition_params`.
/// Request/response envelopes are documented in `README.md`. An unclassifiable
/// rule comes back as `undirected` (not an error); free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_classify(request_json: *const c_char) -> *mut c_char {
    boundary(|| {
        let input = unsafe { read_utf8(request_json, "request") }?;
        envelope::classify_envelope(input)
    })
}

/// Enumerates the canonical condition paths and leaf ids of a condition tree
/// (for timer-pruning hosts). Input/output shapes are documented in
/// `README.md`. Free the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `condition_node_json` must be null or a NUL-terminated string valid for
/// reads for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_leaf_paths(
    condition_node_json: *const c_char,
) -> *mut c_char {
    boundary(|| {
        let input = unsafe { read_utf8(condition_node_json, "condition node") }?;
        envelope::leaf_paths(input)
    })
}

/// Decodes a rule's condition tree into a structured, leaf-id-tagged
/// description for host-rendered condition readouts (ADR 0007). Static: no
/// `SensorContext`, no clock — the host pairs in truth (`evaluate`'s
/// `result.leaves[]`) and observed values itself. Input is the rule's
/// `condition_type` + `condition_params` (the split shape `classify` takes);
/// output is documented in `README.md`. Free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_describe(request_json: *const c_char) -> *mut c_char {
    boundary(|| {
        let input = unsafe { read_utf8(request_json, "request") }?;
        envelope::describe(input)
    })
}

/// Frees a string returned by any other `nocturne_alerts_*` function.
/// Passing null is a no-op. Each pointer must be freed exactly once.
///
/// # Safety
/// `ptr` must be null or a pointer previously returned by this library that
/// has not already been freed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_free_string(ptr: *mut c_char) {
    if ptr.is_null() {
        return;
    }
    drop(unsafe { CString::from_raw(ptr) });
}
