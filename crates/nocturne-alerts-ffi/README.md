# nocturne-alerts-ffi

C ABI wrapper around [`nocturne-alerts-core`](../nocturne-alerts-core): the
alert evaluation engine exposed as JSON-in/JSON-out functions for non-Rust
hosts. The .NET bindings live in `src/Core/Nocturne.Core.Alerts.Native/`
(`AlertsInterop` + `RustAlertEngine`); the Kotlin bindings (Prelude, Android)
are generated with UniFFI behind the optional `uniffi` cargo feature (see
["Kotlin (UniFFI)"](#kotlin-uniffi)) and consume the same contract.

```bash
# from crates/
cargo build --release -p nocturne-alerts-ffi
# -> target/release/nocturne_alerts.dll   (Windows)
# -> target/release/libnocturne_alerts.so (Linux)
```

The crate builds a `cdylib` and a `staticlib`, library name `nocturne_alerts`.

## C ABI

```c
char* nocturne_alerts_version(void);
char* nocturne_alerts_tzdb_version(void);
char* nocturne_alerts_evaluate(const char* request_json);
char* nocturne_alerts_evaluate_node(const char* request_json);
char* nocturne_alerts_leaf_paths(const char* condition_node_json);
char* nocturne_alerts_classify(const char* request_json);
char* nocturne_alerts_references_wall_clock(const char* request_json);
char* nocturne_alerts_describe(const char* request_json);
char* nocturne_alerts_validate(const char* request_json);
void  nocturne_alerts_free_string(char* ptr);
```

- All strings are UTF-8, NUL-terminated. Every returned pointer is owned by
  the caller and must be released with `nocturne_alerts_free_string` exactly
  once (null is a no-op).
- `nocturne_alerts_version` returns the crate version (e.g. `0.1.0`) and
  `nocturne_alerts_tzdb_version` the IANA time zone database release compiled
  in (e.g. `2025b`), both plain strings, not JSON. Everything else returns a
  JSON envelope.
- The library never panics across the boundary and never crashes on bad
  input: panics, null pointers, invalid UTF-8 and malformed JSON all come
  back as the error envelope:

```json
{ "schema_version": 1, "ok": false, "error": "human-readable message" }
```

## Evaluate envelope (`nocturne_alerts_evaluate`)

One call = one rule evaluated for one tick, mirroring the orchestrator
contract (`AlertOrchestrator.EvaluateRuleAsync`: root eval → leaf force-eval
log → excursion tracker → auto-resolve). The engine is stateless between
calls: **all** evaluation state (sustained timers, tracker) is carried in and
out as data, and the host persists it.

The `rule`, `context` and `result` shapes are exactly the golden-corpus
interchange shapes (`ScenarioRule`, `ScenarioContext`, `ExpectedRuleResult` in
`tests/Parity/Nocturne.Alerts.ParityCorpus.Generator/Harness/ScenarioModels.cs`)
— the corpus in `tests/Parity/AlertEngineCorpus/` is the machine-checkable
spec for all of them. Timestamps are RFC 3339 UTC; the engine emits
whole-second instants without a fraction (`2026-01-05T12:00:00Z`) and
preserves sub-second precision when present.

### Request

```jsonc
{
  "schema_version": 1,                      // required; only 1 is accepted
  "rule": {                                 // ScenarioRule corpus shape
    "id": "00000000-0000-0000-0000-000000000001",
    "condition_type": "sustained",          // canonical wire string
    "condition_params": { /* payload */ },  // payload object as stored; null allowed
    "confirmation_readings": 1,             // default 1
    "hysteresis_minutes": 0,                // default 0
    "auto_resolve_enabled": false,          // default false
    "auto_resolve_params": { "type": "…" }  // full ConditionNode or null
  },
  "context": { /* ScenarioContext corpus shape */ },
  "now": "2026-01-05T12:00:00Z",            // the evaluation instant
  "timers": {                               // optional; default {}
    "sustained": "2026-01-05T11:55:00Z"     // condition path -> first-true instant
  },
  "tracker": {                              // optional; absent = never evaluated
    "state": "active",                      // idle|confirming|active|hysteresis; absent = no per-rule state yet
    "confirmation_count": 0,
    "active_excursion_ordinal": 3,          // present only while an excursion is active
    "updated_at": "2026-01-05T11:55:00Z",   // REQUIRED whenever state is present
    "hysteresis_started_at": null,          // set only in hysteresis; absent there = adopt updated_at once
    "next_excursion_ordinal": 4             // default 1; see "State threading"
  },
  "include_leaves": true                    // optional; default true
}
```

`include_leaves: false` skips the leaf log: every leaf is otherwise evaluated
alone on every call, and `result.leaves` is then absent. Nothing else in the
response changes, so a host that does not read `result.leaves` on the live
path should send `false`.

Unknown fields (e.g. the scenario `name`) are ignored, so a corpus
`ScenarioRule` object can be passed as `rule` verbatim.

### Response

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "result": { /* ExpectedRuleResult corpus shape:
                 rule_id, skipped?, root, leaves[], transition, close_reason?,
                 tracker {state, confirmation_count, excursion?,
                          hysteresis_started_at?},
                 auto_resolved?, timer_ops[] */ },
  "timers": { "sustained": "2026-01-05T12:00:00Z" },  // full post-state; persist verbatim
  "tracker": {                                        // full post-state; persist verbatim
    "state": "active",
    "confirmation_count": 0,
    "active_excursion_ordinal": 3,
    "updated_at": "2026-01-05T12:00:00Z",
    "hysteresis_started_at": "…",                     // present only while in hysteresis
    "next_excursion_ordinal": 4
  }
}
```

Failure modes that are **data**, not errors (matching C# engine semantics):
unknown leaf types, unrecognised operators or directions, containers with no
child, null condition records — these evaluate `false` inside `result`.
Envelope-level errors (`ok: false`) are reserved for unusable requests:
malformed JSON, wrong `schema_version`, unknown root `condition_type`, unknown
`tracker.state`, a tracker `state` without `updated_at`, and a rule body that
cannot be evaluated — malformed anywhere in the tree, or one of the shapes in
`docs/alerts/engine-semantics.md` §1.4. That last error reads
`malformed condition_params for '<type>': <reason> at '<path>'`; the host
skips the rule and keeps its timers and tracker unchanged, exactly as the
managed engine's per-rule catch does. An auto-resolve tree that cannot be
evaluated never resolves and is not an error.

`result.skipped` is reserved for a root type with no evaluator (the rule is
skipped like the orchestrator skips it: `result` is `{rule_id, skipped: true}`
and the state passes through unchanged). Every current root type has an
evaluator, so the engine never sets it.

### State threading

- **`timers`** are per-rule: key is the condition path of the `sustained`
  node, value the first-true instant. Persist the response `timers` object and
  send it back on the rule's next evaluation. (It is keyed by path only — the
  rule id is implicit in the call.)
- **`tracker`** per-rule fields (`state`, `confirmation_count`,
  `active_excursion_ordinal`, `updated_at`, `hysteresis_started_at`)
  round-trip the same way and are absent until the rule's first non-skipped
  evaluation. `hysteresis_started_at` is what hysteresis expiry measures
  from; dropping it makes every restore adopt `updated_at`, which slides the
  window forward on each evaluation.
- **`next_excursion_ordinal`** is the 1-based ordinal the next opened
  excursion will receive. It is **shared across all rules** of a tenant (the
  corpus assigns excursion ordinals in creation order across the whole
  scenario), so thread the latest response value into the next call in
  evaluation order, whichever rule it is for. Hosts that key excursions
  differently can ignore the ordinals entirely and treat
  `result.transition` (`opened`/`closed`) as the event source.

## Evaluate node (`nocturne_alerts_evaluate_node`)

Evaluates a single condition tree for one instant **outside** the per-rule
driver: no tracker, no auto-resolve, no leaf log — just the node's truth plus
sustained-timer state threading. This is the FFI counterpart of
`ConditionEvaluatorRegistry.EvaluateNodeAsync` and exists for the auxiliary
evaluation scopes the backend runs against reserved path roots:
smart-snooze conditions (`root: "snooze"`) and the sweep's periodic
auto-resolve (`root: "auto_resolve"`).

### Request

```jsonc
{
  "schema_version": 1,                      // required; only 1 is accepted
  "rule_id": "00000000-0000-0000-0000-000000000001", // keys sustained timers
  "node": { "type": "composite", "composite": { /* … */ } }, // full ConditionNode
  "root": "snooze",                         // optional root path segment;
                                            // defaults to the node's verbatim type
  "context": { /* ScenarioContext corpus shape */ },
  "now": "2026-01-05T12:00:00Z",
  "timers": {                               // optional; default {}
    "snooze": "2026-01-05T11:55:00Z"        // condition path -> first-true instant
  }
}
```

### Response

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "value": true,                            // the node's truth
  "timers": { "snooze": "2026-01-05T12:00:00Z" },  // full post-state; persist verbatim
  "timer_ops": [                            // observable mutations, execution order
    { "op": "set", "path": "snooze", "at": "2026-01-05T12:00:00Z" }
  ]
}
```

Unknown node kinds and missing leaf payloads evaluate `false` (silent-fail
parity). A `node` that is malformed, or that cannot be evaluated
(`docs/alerts/engine-semantics.md` §1.4, with paths under `root`), is an
envelope error (`ok: false`); the C# callers treat it as `false`. Timers are keyed by
the same `(rule_id, path)` identity as `evaluate`; sharing rows between the
per-reading and sweep variants of a scope is intentional (see
`docs/alerts/engine-semantics.md` §2.3).

## Leaf paths (`nocturne_alerts_leaf_paths`)

For timer-pruning hosts (`IConditionTimerStore.PruneToPathsAsync`): given a
condition tree, returns the canonical path of every node slot plus the
leaf-id/path pairs (`LeafIdentity.AssignLeafIds` pre-order ids).

Input is either a full ConditionNode object, or a wrapper that overrides the
root path segment (defaults to the node's verbatim `type` string, matching
`ConditionPath.Walk`; pass `"auto_resolve"` for auto-resolve trees):

```jsonc
{ "type": "composite", "composite": { … } }
// or
{ "root": "auto_resolve", "node": { "type": "sustained", … } }
```

Response:

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "root": "composite",
  "paths": [                                  // every node slot, document order
    "composite",
    "composite[0].sustained",
    "composite[0].sustained[0].threshold",
    "composite[1].iob"
  ],
  "leaves": [                                  // pre-order leaf ids
    { "leaf_id": 0, "path": "composite[0].sustained[0].threshold" },
    { "leaf_id": 1, "path": "composite[1].iob" }
  ]
}
```

Container nodes whose payload/child is missing are leaves (the normative
`LeafIdentity` anomaly); a JSON-null child slot of a composite is a leaf whose
path has an empty type segment (`composite[2].`). Pruning timers to the
`paths` set is always safe — it is a superset of every path a timer can be
keyed under for that tree.

## Classify (`nocturne_alerts_classify`)

Derives a rule's **scope class** for scoped Do Not Disturb (ADR 0004) from its
root `condition_type` + payload-only `condition_params` (exactly the
`alert_rules.condition_params` shape). The .NET host computes and stores this on
rule create/update (`RuleScopeClassifier`) and backfills existing rules once at
startup; a scoped `lows`/`highs` mute then silences a rule only when its class
matches.

Request:

```jsonc
{
  "schema_version": 1,
  "condition_type": "threshold",
  "condition_params": { "direction": "below", "value": 70 }
}
```

Response:

```jsonc
{ "schema_version": 1, "ok": true, "scope_class": "low" }
```

`scope_class` is one of `low | high | composite | undirected`. Unlike
`evaluate`, an **unknown `condition_type` or malformed `condition_params` is not
an error** — `classify` silent-fails to `undirected` (all-only), the safe
default that never lets a scoped mute silence an unclassifiable rule. Only a
structurally malformed *envelope* (bad JSON, wrong `schema_version`) comes back
as the error envelope.

## References wall clock (`nocturne_alerts_references_wall_clock`)

Whether a rule must be evaluated on a timer as well as per reading: its root
kind, or any leaf of its tree, measures elapsed time against an anchor a
reading does not move (`docs/alerts/engine-semantics.md` §5.1). A host that
evaluates only when a reading arrives never fires such a rule while readings
stop, so it schedules these rules on its sweep. The request is the rule body,
as for `classify`:

```jsonc
{
  "schema_version": 1,
  "condition_type": "composite",
  "condition_params": { "operator": "and", "conditions": [ /* … */ ] }
}
```

Response:

```jsonc
{ "schema_version": 1, "ok": true, "references_wall_clock": true }
```

An unknown `condition_type`, or a body that cannot be evaluated, is `false`
(it cannot fire either), not an error; only a malformed envelope is.

## Describe (`nocturne_alerts_describe`)

Decodes a rule's opaque condition tree into a **structured, leaf-id-tagged
description** for a host that renders *condition readouts* — a per-condition
view of whether each leaf is met and how close it is (Prelude, ADR 0007). It is
**static**: no `SensorContext`, no `now`, no truth. The host pairs the
description with each tick's `evaluate` `result.leaves[]` (truth, by `leaf_id`)
and observed values from its own context; the engine just hands back the
decoded operands and the tree shape, so the host never parses the opaque
`condition_params` itself.

Input is the rule's `condition_type` + `condition_params` (the same split shape
`classify` takes):

```jsonc
{
  "schema_version": 1,
  "condition_type": "composite",
  "condition_params": {
    "operator": "and",
    "conditions": [
      { "type": "threshold", "threshold": { "direction": "below", "value": 80 } },
      { "type": "sustained", "sustained": {
          "minutes": 15,
          "child": { "type": "iob", "iob": { "operator": "<", "value": 1 } } } }
    ]
  }
}
```

Response — a recursive `tree`:

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "tree": {
    "type": "composite",
    "path": "composite",
    "operator": "and",                       // and | or (null if unset)
    "conditions": [
      { "leaf_id": 0, "path": "composite[0].threshold",
        "type": "threshold", "kind": "threshold",
        "params": { "direction": "below", "value": 80 } },
      { "type": "sustained", "path": "composite[1].sustained", "minutes": 15,
        "child": { "leaf_id": 1, "path": "composite[1].sustained[0].iob",
                   "type": "iob", "kind": "iob",
                   "params": { "operator": "<", "value": 1 } } }
    ]
  }
}
```

- Every node carries its canonical `path` (the same form `leaf_paths` emits). A
  `sustained` node's `path` equals the key the engine stores its timer under
  (`condition_timers.path`), so a host joins a duration node straight to its
  persisted first-true instant for a "11 of 15 min" countdown.
- **Containers** (`composite`, `not`, `sustained`) carry structure only —
  `operator` / `minutes` / nested `conditions`/`child` — and **no `leaf_id`**.
- **Leaves** carry `leaf_id`, the verbatim `type`, the resolved canonical
  `kind` (or `null` for an unknown/`null` slot), and decoded `params`.
- **Leaf ids match `evaluate` exactly.** The walk is the same pre-order
  `collect_leaves` over the same reconstituted node, so a malformed container
  (missing `child`/`conditions`) collapses to a single leaf and a JSON-`null`
  composite slot is a typeless leaf — identical to the force-eval log.
- **Operands are decoded for rendering:** enum ordinals become wire names
  (`days: [0, 6]` → `["Sunday", "Saturday"]`); decimal operands round-trip
  **exactly** (the crate's `serde_json` uses `arbitrary_precision`).
- Like `evaluate`, an **unknown `condition_type`** (or a malformed envelope /
  wrong `schema_version`) is an error envelope. A malformed *payload* is not —
  it collapses to a single best-effort leaf with default operands, mirroring
  the engine's silent-fail.

## Validate (`nocturne_alerts_validate`)

The save-time check of a rule's condition trees: everything
`docs/alerts/engine-semantics.md` §1.4 rejects, both the shapes that cannot be
evaluated and the ones that evaluate but can never mean what was written
(unknown kinds, unrecognised operators, empty groups, …). Hosts call it before
storing a rule and refuse the save when `valid` is false; the backend does so in
`AlertRulesController` (a 400).

Request:

```jsonc
{
  "schema_version": 1,
  "condition_type": "composite",                     // wire name, checked exactly
  "condition_params": { "operator": "and", "conditions": [ /* … */ ] },
  "auto_resolve_params": { "type": "threshold", /* … */ },  // optional; null/absent = not checked
  "snooze_conditions": [ /* ConditionNode, … */ ]    // optional; checked as composite{and}
}
```

Pass `auto_resolve_params` only when auto-resolve is enabled, and
`snooze_conditions` only when smart snooze is on — the trees the rule actually
evaluates. An empty `snooze_conditions` list is valid (the trend fallback).

Response:

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "valid": false,
  "issues": [
    { "scope": "condition", "path": "composite[1].", "reason": "condition_missing", "field": null },
    { "scope": "snooze", "path": "snooze[0].sustained", "reason": "minutes_not_positive", "field": "minutes" }
  ]
}
```

`scope` is `condition`, `auto_resolve` or `snooze`; `path` is the offending
node's condition path under that scope's root (a null slot's path ends in
`.`); `reason` is a stable code from §1.4 and `field` the payload field it is
on, when there is one. Neither ever carries a payload value. A malformed tree
reports only its first structural error, as the reader stops there. A rule with
issues is `ok: true`; only an unusable envelope is the error envelope.

## Kotlin (UniFFI)

The optional `uniffi` cargo feature adds a [UniFFI](https://mozilla.github.io/uniffi-rs/)
proc-macro surface (`src/uniffi_api.rs`) for the Android app (Prelude). It is
**off by default** — the plain C ABI build used by the backend never gains the
uniffi dependency. uniffi is pinned exactly (`=0.31.1`): the generated Kotlin
bindings and the compiled library must come from the same uniffi version.

The Kotlin surface is deliberately JSON-in/JSON-out — the **same envelope
documented above is the contract for both consumers** (no parallel typed
surface that could drift). Each function runs the same internal handler as its
C counterpart, with the same panic guard (panics and unusable requests come
back as the `ok: false` envelope, never as an exception):

```kotlin
package uniffi.nocturne_alerts

fun evaluate(requestJson: String): String             // nocturne_alerts_evaluate
fun evaluateNode(requestJson: String): String         // nocturne_alerts_evaluate_node
fun classify(requestJson: String): String             // nocturne_alerts_classify
fun referencesWallClock(requestJson: String): String  // nocturne_alerts_references_wall_clock
fun leafPaths(requestJson: String): String            // nocturne_alerts_leaf_paths
fun describe(requestJson: String): String             // nocturne_alerts_describe
fun validate(requestJson: String): String             // nocturne_alerts_validate
fun version(): String                                 // plain string, not JSON
fun tzdbVersion(): String                             // plain string, not JSON
```

Memory is managed by the generated bindings (no `free` counterpart needed).
The bindings use JNA (`net.java.dev.jna`) and load the library named
`nocturne_alerts` (`libnocturne_alerts.so` in the APK's `jniLibs`).

### Generating the bindings

The crate ships the standard in-crate `uniffi-bindgen` binary (gated on the
`bindgen` feature, which implies `uniffi` + `uniffi/cli`). Library mode reads
the UniFFI metadata out of the compiled artifact, so build first:

```bash
# from crates/
cargo build -p nocturne-alerts-ffi --features uniffi
cargo run -p nocturne-alerts-ffi --features bindgen --bin uniffi-bindgen -- \
  generate --library target/debug/libnocturne_alerts.so \
  --language kotlin --out-dir target/uniffi/kotlin
# (on Windows the library is target/debug/nocturne_alerts.dll)
# -> target/uniffi/kotlin/uniffi/nocturne_alerts/nocturne_alerts.kt
```

Generated bindings are build outputs — do not commit them; Prelude's CI
regenerates them from the same library it ships. The default Kotlin package is
`uniffi.nocturne_alerts`; override it with a `uniffi.toml`
(`[bindings.kotlin] package_name = "…"`) if Prelude needs a different
namespace.

### Android (.so) builds — Prelude CI

The Android shared objects are cross-compiled in Prelude's CI with
[`cargo-ndk`](https://github.com/bbqsrc/cargo-ndk) (requires the Android NDK;
run from `crates/`):

```bash
cargo ndk -t arm64-v8a -t armeabi-v7a -t x86_64 -o jniLibs build --release \
  -p nocturne-alerts-ffi --features uniffi
```

CI must generate the Kotlin bindings from the **same build** (same crate
revision, same uniffi version) as the `.so` files it packages — the bindings
checksum the API at load time and refuse a mismatched library.

## Versioning

`schema_version` covers the envelope layer. The time zone rules `time_of_day`
and `day_of_week` apply are the IANA release compiled into the library
(`chrono-tz`), not the host's; `nocturne_alerts_tzdb_version` reports it, so a
host can log it at load and notice when it falls behind a zone change. Keeping
it current means updating the `chrono-tz` dependency and rebuilding. Behavioural changes to evaluation
itself are governed by the golden corpus: both the Rust core (`tests/parity.rs`),
this crate's FFI round-trip test, and the .NET three-way suite
(`tests/Unit/Nocturne.Alerts.Native.Tests`) pin every scenario against the
committed `.expected.json` snapshots.
