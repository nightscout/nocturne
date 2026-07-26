# nocturne-alerts-core

Pure-Rust port of Nocturne's leaf/node alert evaluation engine, shared (eventually)
by the .NET backend (via FFI) and Prelude on Android (via UniFFI). The port is
**behaviourally exact** against the C# implementation — including behaviours that
look like bugs. The normative spec is
[`docs/alerts/engine-semantics.md`](../../docs/alerts/engine-semantics.md); every
quirk in it (and its anomalies index) is reproduced deliberately. The
machine-checkable form of the spec is the golden corpus in
[`tests/Parity/AlertEngineCorpus/`](../../tests/Parity/AlertEngineCorpus/),
exercised scenario-by-scenario by `tests/parity.rs`.

## Boundary

The crate is **pure evaluation**: no I/O, no clock, no persistence.

- `now` is always passed in; nothing reads wall-clock time.
- The sensor context ([`context::SensorContext`]) is plain input data assembled
  host-side (backend enricher / Prelude's local assembler).
- Stateful pieces live in caller-owned in-memory stores the host is responsible
  for persisting between evaluations:
  - sustained-condition timers — [`sustained::TimerStore`], keyed by
    `(rule_id, condition_path)` and recording observable set/clear mutations;
  - the excursion state machine — [`excursion::ExcursionTracker`]
    (idle → confirming → active → hysteresis), including the sliding
    `UpdatedAt` hysteresis-expiry proxy, verbatim.
- Host-side and out of scope: context enrichment, persistence, delivery/DND
  dispatch suppression, sweep scheduling, the `signal_loss` watchdog (the crate
  treats `signal_loss` as an unknown kind — false inside trees, skipped as a
  root type), and the sweep's hysteresis force-close.

## Layout

| Module | Contents |
|---|---|
| `model` | serde-compatible `ConditionNode` + payload records, parsed with System.Text.Json semantics (snake_case, case-insensitive properties, constructor defaults, `JsonException` ⇒ `ParseError`) |
| `context` | `SensorContext` as plain data, deserialisable from the corpus scenario context wire format |
| `leaf_identity` | pre-order DFS leaf-id assignment (containers unwrapped; malformed containers ARE leaves) |
| `paths` | `ConditionPath` grammar: root segment verbatim, children `[i].{type-as-written}` (casing preserved) |
| `compare` | `ComparisonOps` on `rust_decimal`; `TimeSpan.TotalX` reciprocal-multiplication math; the C# `(decimal)double` 15-significant-digit cast |
| `eval` | one module per leaf family plus container evaluation (composite short-circuit, `not`, dispatch) |
| `sustained` | timer state in/out and the `sustained` container |
| `excursion` | excursion tracker state machine incl. force-close |
| `engine` | `evaluate_rule` driver mirroring the orchestrator contract (root eval → leaf force-eval → tracker → unconditional auto-resolve), plus `evaluate_snooze_conditions` for the sweep's smart-snooze predicate under the `snooze` path root |

## Verification

```bash
cd crates
cargo test --workspace                          # parity corpus (107 scenarios) + unit tests
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --check
```

The parity suite fails loudly (scenario / tick / rule / field) on any divergence
from the committed `.expected.json` snapshots. Do not "fix" a divergence by
editing the corpus — it is generated only by
`tests/Parity/Nocturne.Alerts.ParityCorpus.Generator` while the C# engine is
authoritative.
