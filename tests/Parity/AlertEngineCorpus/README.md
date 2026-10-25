# Alert Engine Golden Corpus

Machine-checkable form of [docs/alerts/engine-semantics.md](../../../docs/alerts/engine-semantics.md):
the normative behaviour of the leaf/node alert evaluation engine, the shared Rust crate
(`nocturne-alerts-core`) consumed by both the backend and Prelude (Android).

Each scenario is a pair of files:

- `<name>.json` — the scenario: alert rules (condition trees as stored in
  `alert_rules.condition_params`) plus a sequence of evaluation ticks, each with an
  explicit `now` and a fully-specified sensor context.
- `<name>.expected.json` — the snapshot: per tick, per rule — root condition truth,
  per-leaf force-eval truths (replay leaf-log semantics), excursion tracker transition
  and state, auto-resolve outcome, and sustained-timer set/clear operations with their
  `(rule, path)` keys.

`replay/` holds the replay driver's scenarios (engine-semantics.md §8) in the same pair form:
`<name>.json` is a rule set plus the ticks to replay it over, each with its context and the
rules Do Not Disturb suppresses a fire for; `<name>.expected.json` is the evaluation order, the
fire/clear/auto-resolve events, the leaf log, and every rule's state on every tick.

**The corpus is frozen.** The snapshots were generated from the C# engine the crate
replaced, and a committed `.expected.json` is never regenerated: a change in behaviour fails
the parity suites until this corpus and `docs/alerts/engine-semantics.md` change with it,
deliberately.

To add a scenario, write `<name>.json` by hand (in `replay/` for a replay scenario), then
have the engine write its snapshot and review that file like code:

```bash
cd crates
NOCTURNE_CORPUS_BLESS=1 cargo test -p nocturne-alerts-core --test parity --test replay_parity
```

Only a scenario with no `.expected.json` is written; without `NOCTURNE_CORPUS_BLESS=1` a
missing snapshot fails the suite.

Consumers:

1. `crates/nocturne-alerts-core` Rust tests (`tests/parity.rs`, `tests/replay_parity.rs`),
2. the .NET FFI parity suite (`tests/Unit/Nocturne.Alerts.Native.Tests`),
3. the backend's engine-seam corpus tests (`tests/Unit/Nocturne.API.Tests`, `Services/Alerts/Engines`),
4. Prelude's Kotlin instrumented corpus test (via UniFFI bindings).

After the backend cuts over to the Rust engine, the snapshots become the single
normative spec, regenerated only by the Rust engine.
