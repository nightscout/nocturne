//! `validate`: the save-time check of a rule's condition trees, documented in
//! the crate `README.md`.

use serde::Deserialize;
use serde_json::{Value, json};

use nocturne_alerts_core::model::ParseError;
use nocturne_alerts_core::paths::{AUTO_RESOLVE_ROOT, SNOOZE_ROOT};
use nocturne_alerts_core::validate::{validate_node, validate_rule};

use crate::envelope::SCHEMA_VERSION;

#[derive(Deserialize)]
struct ValidateRequest {
    schema_version: i64,
    condition_type: String,
    #[serde(default)]
    condition_params: Value,
    /// A full ConditionNode, checked when present and not null.
    #[serde(default)]
    auto_resolve_params: Option<Value>,
    /// `client_configuration.snooze.conditions`, checked as the
    /// `composite{and}` the sweep evaluates them as.
    #[serde(default)]
    snooze_conditions: Option<Vec<Value>>,
}

pub fn validate(request_json: &str) -> Result<Value, String> {
    let req: ValidateRequest =
        serde_json::from_str(request_json).map_err(|e| format!("invalid request envelope: {e}"))?;
    if req.schema_version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported schema_version {} (expected {SCHEMA_VERSION})",
            req.schema_version
        ));
    }

    let mut issues = Vec::new();
    push(
        &mut issues,
        "condition",
        validate_rule(&req.condition_type, &req.condition_params),
    );
    if let Some(node) = req.auto_resolve_params.filter(|v| !v.is_null()) {
        push(
            &mut issues,
            "auto_resolve",
            validate_node(&node, AUTO_RESOLVE_ROOT),
        );
    }
    if let Some(conditions) = req.snooze_conditions.filter(|c| !c.is_empty()) {
        let wrapped = json!({
            "type": "composite",
            "composite": { "operator": "and", "conditions": conditions },
        });
        push(&mut issues, "snooze", validate_node(&wrapped, SNOOZE_ROOT));
    }

    Ok(json!({
        "schema_version": SCHEMA_VERSION,
        "ok": true,
        "valid": issues.is_empty(),
        "issues": issues,
    }))
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
