//! `migrate`: one stored condition tree rewritten from schema 1 to schema 2
//! (engine-semantics.md §1.5), documented in the crate `README.md`.

use serde::Deserialize;
use serde_json::{Value, json};

use nocturne_alerts_core::migrate::{Migration, TreeRoot, migrate_v1_to_v2};

use crate::envelope::{ok, read_request};

#[derive(Deserialize)]
struct MigrateRequest {
    schema_version: i64,
    /// The tree: a rule body when `condition_type` is present, else a full
    /// condition node.
    #[serde(default)]
    tree: Value,
    /// The kind a rule body is the payload of.
    #[serde(default)]
    condition_type: Option<String>,
    /// The root segment of a full node's paths (`auto_resolve`, `snooze`).
    #[serde(default)]
    root: Option<String>,
}

pub(crate) fn migrate(request_json: &str) -> Result<Value, String> {
    let req: MigrateRequest = read_request(request_json, |r: &MigrateRequest| r.schema_version)?;
    let root = match (&req.condition_type, &req.root) {
        (Some(kind), None) => TreeRoot::Body(kind),
        (None, Some(root)) => TreeRoot::Node(root),
        _ => return Err("exactly one of 'condition_type' and 'root' is required".to_owned()),
    };
    Ok(ok(match migrate_v1_to_v2(&req.tree, root) {
        Ok(Migration::Current) => json!({ "outcome": "current" }),
        Ok(Migration::Migrated(m)) => json!({
            "outcome": "migrated",
            "tree": m.tree,
            "rounded": m.rounded,
        }),
        Err(e) => json!({
            "outcome": "failed",
            "issue": {
                "path": e.path,
                "reason": e.reason.code(),
                "field": e.reason.field(),
            },
        }),
    }))
}
