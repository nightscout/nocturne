//! `validate`: the save-time check of a rule's condition trees, documented in
//! the crate `README.md`.

use serde::Deserialize;
use serde_json::{Map, Value, json};

use nocturne_alerts_core::model::ParseError;
use nocturne_alerts_core::paths::{AUTO_RESOLVE_ROOT, SNOOZE_ROOT};
use nocturne_alerts_core::validate::{
    UnknownKey, unknown_keys_in_node, unknown_keys_in_rule, validate_node, validate_rule,
};

use crate::envelope::{ok, read_request};

#[derive(Deserialize)]
struct ValidateRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
    /// A full condition node, checked when present and not null.
    #[serde(default)]
    auto_resolve_params: Option<Value>,
    /// Smart-snooze conditions, checked as the `composite{and}` they
    /// evaluate as.
    #[serde(default)]
    snooze_conditions: Option<Vec<Value>>,
    /// The rule as stored, when the request is an edit of it.
    #[serde(default)]
    stored: Option<StoredTrees>,
}

/// Every tree the stored rule holds, whether or not it is evaluated.
#[derive(Deserialize)]
struct StoredTrees {
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
    #[serde(default)]
    auto_resolve_params: Option<Value>,
    #[serde(default)]
    snooze_conditions: Option<Vec<Value>>,
}

pub(crate) fn validate(request_json: &str) -> Result<Value, String> {
    let req: ValidateRequest = read_request(request_json, |r: &ValidateRequest| r.schema_version)?;

    let mut body = req.condition_params;
    let mut auto_resolve = req.auto_resolve_params.filter(|v| !v.is_null());
    let mut snooze = req
        .snooze_conditions
        .filter(|c| !c.is_empty())
        .map(snooze_node);

    let mut stripped = Vec::new();
    let mut body_stripped = false;
    let mut auto_resolve_stripped = false;
    let mut snooze_stripped = false;
    if let Some(stored) = &req.stored {
        let found = unknown_keys_in_rule(&req.condition_type, &body);
        body_stripped = strip(
            &mut body,
            "condition",
            found,
            &unknown_keys_in_rule(&stored.condition_type, &stored.condition_params),
            &mut stripped,
        );
        if let Some(tree) = &mut auto_resolve {
            let before = stored
                .auto_resolve_params
                .as_ref()
                .filter(|v| !v.is_null())
                .map(|v| unknown_keys_in_node(v, AUTO_RESOLVE_ROOT))
                .unwrap_or_default();
            let found = unknown_keys_in_node(tree, AUTO_RESOLVE_ROOT);
            auto_resolve_stripped = strip(tree, "auto_resolve", found, &before, &mut stripped);
        }
        if let Some(tree) = &mut snooze {
            let before = stored
                .snooze_conditions
                .clone()
                .filter(|c| !c.is_empty())
                .map(|c| unknown_keys_in_node(&snooze_node(c), SNOOZE_ROOT))
                .unwrap_or_default();
            let found = unknown_keys_in_node(tree, SNOOZE_ROOT);
            snooze_stripped = strip(tree, "snooze", found, &before, &mut stripped);
        }
    }

    let mut issues = Vec::new();
    push(
        &mut issues,
        "condition",
        validate_rule(&req.condition_type, &body),
    );
    if let Some(node) = &auto_resolve {
        push(
            &mut issues,
            "auto_resolve",
            validate_node(node, AUTO_RESOLVE_ROOT),
        );
    }
    if let Some(node) = &snooze {
        push(&mut issues, "snooze", validate_node(node, SNOOZE_ROOT));
    }

    let mut response = Map::new();
    response.insert("valid".into(), issues.is_empty().into());
    response.insert("issues".into(), issues.into());
    if req.stored.is_some() {
        response.insert("stripped".into(), stripped.into());
        if body_stripped {
            response.insert("condition_params".into(), body);
        }
        if auto_resolve_stripped && let Some(tree) = auto_resolve {
            response.insert("auto_resolve_params".into(), tree);
        }
        if snooze_stripped
            && let Some(mut tree) = snooze
            && let Some(conditions) = tree.pointer_mut("/composite/conditions")
        {
            response.insert("snooze_conditions".into(), conditions.take());
        }
    }
    Ok(ok(Value::Object(response)))
}

fn snooze_node(conditions: Vec<Value>) -> Value {
    json!({
        "type": "composite",
        "composite": { "operator": "and", "conditions": conditions },
    })
}

/// Removes from `tree` each of `found` that the stored tree also has, at the
/// same condition path, in the same object and under the same name, and
/// records it in `stripped`. True when any was removed.
fn strip(
    tree: &mut Value,
    scope: &str,
    found: Vec<UnknownKey>,
    stored: &[UnknownKey],
    stripped: &mut Vec<Value>,
) -> bool {
    let mut any = false;
    for key in found {
        if !stored.contains(&key) {
            continue;
        }
        let removed = tree
            .pointer_mut(&key.pointer)
            .and_then(Value::as_object_mut)
            .and_then(|o| o.shift_remove(&key.key));
        if removed.is_some() {
            stripped.push(json!({ "scope": scope, "path": key.path, "field": key.key }));
            any = true;
        }
    }
    any
}

fn push(issues: &mut Vec<Value>, scope: &str, found: Vec<ParseError>) {
    issues.extend(found.into_iter().map(|e| {
        json!({
            "scope": scope,
            "path": e.path,
            "reason": e.reason.code(),
            "field": e.reason.field(),
        })
    }));
}
