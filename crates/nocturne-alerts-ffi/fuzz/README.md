# nocturne-alerts-fuzz

`cargo fuzz` targets for the alert engine: `node_parse` (`Node::parse`), `parse_payload`, and one
`ffi_*` target for each C entry point that takes host bytes. The FFI targets check that the answer
is UTF-8 JSON with a boolean `ok`, that it is freed through `nocturne_alerts_free_string`, that
the panic guard never caught anything, that two calls answer the same, and that a null pointer
answers an error envelope.

This directory is its own workspace, below the ffi crate and not directly under `crates/`, so the
.NET build's freshness check of the shipped library (`LocalIsFresh`) does not read its manifest.
Its `Cargo.lock` is `crates/Cargo.lock` plus the fuzz crate's own dependencies, and
`tests/fuzz_lock.rs` fails when a shared dependency drifts from the shipped version. The stable `crates` workspace never builds it, and it needs a
nightly toolchain: `.github/workflows/alerts-fuzz.yml` pins the dated nightly and runs each target
for a minute on a change to `crates/` and every week.

```bash
cd crates
NOCTURNE_FUZZ_SEEDS="$PWD/fuzz/corpus" \
  cargo test -p nocturne-alerts-ffi --test envelope_properties write_fuzz_seeds -- --ignored
cd fuzz
cargo +nightly-2026-11-10 fuzz run ffi_evaluate
```

The seeds come from the property generators and the golden corpus, and are not committed.
