//! The fuzz crate's lockfile pins every shared dependency to the version the workspace lockfile
//! ships, so the targets attack the parsers that run in production.

#![allow(
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic,
    clippy::indexing_slicing,
    reason = "test code"
)]

use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;

fn locked(path: &Path) -> BTreeMap<String, BTreeSet<String>> {
    let text = std::fs::read_to_string(path).unwrap();
    let mut packages: BTreeMap<String, BTreeSet<String>> = BTreeMap::new();
    let mut name = None;
    for line in text.lines() {
        if let Some(n) = line.strip_prefix("name = \"") {
            name = Some(n.trim_end_matches('"').to_owned());
        } else if let (Some(v), Some(n)) = (line.strip_prefix("version = \""), name.take()) {
            packages
                .entry(n)
                .or_default()
                .insert(v.trim_end_matches('"').to_owned());
        }
    }
    packages
}

/// Regenerate the fuzz lock after a change to `crates/Cargo.lock` with
/// `cp crates/Cargo.lock crates/nocturne-alerts-ffi/fuzz/Cargo.lock` and a `cargo metadata` in the
/// fuzz directory, which keeps every locked version and adds only the fuzz crate's own.
#[test]
fn the_fuzz_lock_resolves_shared_dependencies_to_the_shipped_versions() {
    let ffi = Path::new(env!("CARGO_MANIFEST_DIR"));
    let shipped = locked(&ffi.join("../Cargo.lock"));
    let fuzzed = locked(&ffi.join("fuzz/Cargo.lock"));
    let drifted: Vec<String> = fuzzed
        .iter()
        .filter_map(|(name, versions)| {
            let wanted = shipped.get(name)?;
            (!versions.is_subset(wanted))
                .then(|| format!("{name}: fuzz {versions:?}, shipped {wanted:?}"))
        })
        .collect();
    assert!(drifted.is_empty(), "fuzz lock drifted: {drifted:#?}");
    assert!(fuzzed.contains_key("serde_json") && fuzzed.contains_key("rust_decimal"));
}
