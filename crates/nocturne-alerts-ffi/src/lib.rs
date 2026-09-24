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
use std::ffi::{CStr, CString, c_char};
use std::panic::{AssertUnwindSafe, catch_unwind};

use serde_json::{Value, json};

use envelope::SCHEMA_VERSION;

type Handler = fn(&str) -> Result<Value, String>;

fn error_json(message: &str) -> String {
    json!({ "schema_version": SCHEMA_VERSION, "ok": false, "error": message }).to_string()
}

/// JSON escapes control characters, so only a non-JSON string can hold a NUL.
fn into_c_string(s: String) -> *mut c_char {
    CString::new(s)
        .or_else(|_| CString::new(error_json("response contained an interior NUL byte")))
        .unwrap_or_default()
        .into_raw()
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

/// Reads `input` as UTF-8, runs `handler` on it inside the panic guard, and
/// returns the envelope as a caller-owned C string.
///
/// # Safety
/// `input` must be null or point to a NUL-terminated string valid for reads
/// for the duration of the call.
unsafe fn entry(input: *const c_char, what: &str, handler: Handler) -> *mut c_char {
    into_c_string(envelope_string(|| {
        if input.is_null() {
            return Err(format!("{what} pointer is null"));
        }
        // SAFETY: `input` is non-null, and the caller guarantees it is
        // NUL-terminated and readable for this call; the borrow ends with it.
        let text = unsafe { CStr::from_ptr(input) }.to_str();
        handler(text.map_err(|_| format!("{what} is not valid UTF-8"))?)
    }))
}

/// Returns the crate version as a plain, non-JSON string.
/// Free with [`nocturne_alerts_free_string`].
#[unsafe(no_mangle)]
pub extern "C" fn nocturne_alerts_version() -> *mut c_char {
    into_c_string(env!("CARGO_PKG_VERSION").to_owned())
}

/// Evaluates one rule for one tick. Free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_evaluate(request_json: *const c_char) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", envelope::evaluate) }
}

/// Evaluates one condition node for one instant outside the per-rule driver:
/// smart-snooze conditions and the sweep's auto-resolve. Free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_evaluate_node(request_json: *const c_char) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", envelope::evaluate_node) }
}

/// Derives a rule's scope class for scoped Do Not Disturb. Free the result
/// with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_classify(request_json: *const c_char) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", envelope::classify_rule) }
}

/// Enumerates the condition paths and leaf ids of a condition tree. Free the
/// result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `condition_node_json` must be null or a NUL-terminated string valid for
/// reads for the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_leaf_paths(
    condition_node_json: *const c_char,
) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(condition_node_json, "condition node", envelope::leaf_paths) }
}

/// Decodes a rule's condition tree into a leaf-id-tagged description for
/// condition readouts. Free the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_describe(request_json: *const c_char) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", envelope::describe) }
}

/// Checks a rule's condition trees for everything a save should reject. Free
/// the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_validate(request_json: *const c_char) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", validate_envelope::validate) }
}

/// Frees a string returned by any other `nocturne_alerts_*` function. Null is
/// a no-op.
///
/// # Safety
/// `ptr` must be null or a pointer returned by this library that has not
/// already been freed.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_free_string(ptr: *mut c_char) {
    if !ptr.is_null() {
        // SAFETY: the caller guarantees `ptr` came from `CString::into_raw`
        // in this library and is freed once.
        drop(unsafe { CString::from_raw(ptr) });
    }
}
