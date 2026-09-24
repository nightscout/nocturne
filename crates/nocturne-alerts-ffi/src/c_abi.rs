//! The exported C functions.

#![expect(unsafe_code, reason = "a C ABI takes and returns raw pointers")]

use std::ffi::{CStr, CString, c_char};

use serde_json::Value;

use crate::{envelope, envelope_string, error_json, tracker_envelope, validate_envelope};

type Handler = fn(&str) -> Result<Value, String>;

/// JSON escapes control characters, so only a non-JSON string can hold a NUL.
fn into_c_string(s: String) -> *mut c_char {
    CString::new(s)
        .or_else(|_| CString::new(error_json("response contained an interior NUL byte")))
        .unwrap_or_default()
        .into_raw()
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

/// Returns the IANA time zone database release compiled into the library
/// (e.g. `2025b`) as a plain, non-JSON string. Free with
/// [`nocturne_alerts_free_string`].
#[unsafe(no_mangle)]
pub extern "C" fn nocturne_alerts_tzdb_version() -> *mut c_char {
    into_c_string(nocturne_alerts_core::TZDB_VERSION.to_owned())
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

/// Whether a rule must also be evaluated on a timer, not only per reading.
/// Free the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_references_wall_clock(
    request_json: *const c_char,
) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", envelope::wall_clock) }
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

/// Advances one rule's excursion tracker for one evaluation, without
/// evaluating a condition tree. Free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_tracker_process(
    request_json: *const c_char,
) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", tracker_envelope::process) }
}

/// Closes one rule's excursion, if it has one, from any tracker state. Free
/// the result with [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_tracker_force_close(
    request_json: *const c_char,
) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe { entry(request_json, "request", tracker_envelope::force_close) }
}

/// Closes one rule's excursion when its hysteresis window has elapsed,
/// without an evaluation. Free the result with
/// [`nocturne_alerts_free_string`].
///
/// # Safety
/// `request_json` must be null or a NUL-terminated string valid for reads for
/// the duration of the call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn nocturne_alerts_tracker_close_elapsed_hysteresis(
    request_json: *const c_char,
) -> *mut c_char {
    // SAFETY: forwarded from this function's contract.
    unsafe {
        entry(
            request_json,
            "request",
            tracker_envelope::close_elapsed_hysteresis,
        )
    }
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
