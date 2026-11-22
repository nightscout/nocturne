//! Checks the fuzz targets share. A target is a one-line call into this crate, so
//! what each one asserts is read in one place.

use std::ffi::{CStr, CString, c_char};

use nocturne_alerts_core::context::SensorContext;
use nocturne_alerts_core::enums::WireEnum;
use nocturne_alerts_core::eval::{Env, eval_node};
use nocturne_alerts_core::model::{ConditionKind, Node, parse_payload};
use nocturne_alerts_core::sustained::TimerStore;
use serde_json::Value;
use uuid::Uuid;

type Entry = unsafe extern "C" fn(*const c_char) -> *mut c_char;

fn json(data: &[u8]) -> Option<Value> {
    serde_json::from_slice(data).ok()
}

/// `Node::parse` on any JSON never panics, never answers `Err` with no issue, and a node it
/// accepts evaluates without panicking against an empty context.
pub fn check_node_parse(data: &[u8]) {
    let Some(doc) = json(data) else { return };
    match Node::parse(&doc) {
        Ok(node) => {
            let context = SensorContext::default();
            let mut timers = TimerStore::new();
            let now = chrono::DateTime::UNIX_EPOCH;
            let mut env = Env::new(now, Uuid::nil(), &context, &mut timers);
            eval_node(&node, node.kind().name(), &mut env);
        }
        Err(issues) => assert!(!issues.0.is_empty(), "an unevaluable node with no issue"),
    }
}

/// `parse_payload` on any JSON, for the kind the first byte selects, never panics and never
/// answers `Err` with no issue.
pub fn check_parse_payload(data: &[u8]) {
    let Some((first, rest)) = data.split_first() else {
        return;
    };
    let kinds = ConditionKind::ALL;
    let Some(kind) = kinds.get(usize::from(*first) % kinds.len()) else {
        return;
    };
    let Some(doc) = json(rest) else { return };
    if let Err(issues) = parse_payload(*kind, &doc) {
        assert!(!issues.0.is_empty(), "an unevaluable payload with no issue");
    }
}

/// Calls `entry` as a host does, with bytes up to the first NUL, and returns the envelope after
/// freeing the buffer through the library's own free function.
fn call(entry: Entry, input: *const c_char) -> String {
    // SAFETY: `input` is null or a NUL-terminated buffer that outlives the call.
    let out = unsafe { entry(input) };
    assert!(!out.is_null(), "an entry point returned null");
    // SAFETY: a non-null result is a NUL-terminated string the library owns until freed.
    let text = unsafe { CStr::from_ptr(out) }
        .to_str()
        .expect("a response that is not UTF-8")
        .to_owned();
    // SAFETY: `out` came from this library and is freed once, here.
    unsafe { nocturne_alerts::nocturne_alerts_free_string(out) };
    text
}

fn assert_envelope(text: &str) {
    let envelope: Value = serde_json::from_str(text).expect("a response that is not JSON");
    let ok = envelope
        .get("ok")
        .and_then(Value::as_bool)
        .expect("an envelope without a boolean ok");
    if !ok {
        let error = envelope
            .get("error")
            .and_then(Value::as_str)
            .expect("an error envelope without a message");
        assert!(
            !error.starts_with("panic in alert engine"),
            "the panic guard caught: {error}"
        );
    }
}

/// A C entry point given any bytes answers a JSON envelope, never through its panic guard,
/// answers the same twice, and answers a null pointer with an error envelope.
pub fn check_entry(entry: Entry, data: &[u8]) {
    let text = data.split(|b| *b == 0).next().unwrap_or_default();
    let input = CString::new(text).expect("bytes up to the first NUL hold no NUL");
    let first = call(entry, input.as_ptr());
    assert_envelope(&first);
    assert_eq!(first, call(entry, input.as_ptr()), "two answers differ");

    let null = call(entry, std::ptr::null());
    assert_envelope(&null);
    assert!(null.contains("\"ok\":false"), "a null pointer was accepted");
}
