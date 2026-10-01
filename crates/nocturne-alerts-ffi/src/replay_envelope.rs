//! The replay envelope, documented in the crate `README.md`: the replay driver
//! (engine-semantics.md §8) over a whole series of ticks in one call.

use serde::Deserialize;
use serde_json::Value;

use nocturne_alerts_core::context::check_timestamp;
use nocturne_alerts_core::engine::{Rule, WireRule};
use nocturne_alerts_core::replay::{ReplayOptions, ReplayTick, replay as run_replay};

use crate::envelope::{ok, read_request};

#[derive(Deserialize)]
struct ReplayRequest {
    schema_version: i64,
    rules: Vec<WireRule>,
    ticks: Vec<ReplayTick>,
    #[serde(default)]
    include_ticks: bool,
}

/// A rule body that cannot be evaluated is skipped on every tick, not an
/// error.
pub(crate) fn replay(request_json: &str) -> Result<Value, String> {
    let req: ReplayRequest = read_request(request_json, |r: &ReplayRequest| r.schema_version)?;
    let rules = req
        .rules
        .into_iter()
        .map(Rule::try_from)
        .collect::<Result<Vec<_>, _>>()?;
    for tick in &req.ticks {
        check_timestamp(tick.at, "ticks.at")?;
    }
    let options = ReplayOptions {
        include_ticks: req.include_ticks,
    };
    let outcome = run_replay(&rules, req.ticks, options).map_err(|e| e.to_string())?;
    Ok(ok(outcome.to_json()))
}
