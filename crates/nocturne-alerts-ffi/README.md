# nocturne-alerts-ffi

C ABI wrapper around [`nocturne-alerts-core`](../nocturne-alerts-core): the
alert evaluation engine exposed as JSON-in/JSON-out functions for non-Rust
hosts. The .NET bindings live in `src/Core/Nocturne.Core.Alerts.Native/`
(`AlertsInterop` + `RustAlertEngine`); the Kotlin bindings (Prelude, Android)
are generated with UniFFI behind the optional `uniffi` cargo feature (see
["Kotlin (UniFFI)"](#kotlin-uniffi)), as are the Swift bindings for iOS hosts
(see ["Swift (UniFFI)"](#swift-uniffi)); all consume the same contract.

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
int64_t nocturne_alerts_clock_tolerance_ms(void);
int64_t nocturne_alerts_hold_bound_ms(void);
char* nocturne_alerts_evaluate(const char* request_json);
char* nocturne_alerts_evaluate_node(const char* request_json);
char* nocturne_alerts_leaf_paths(const char* condition_node_json);
char* nocturne_alerts_classify(const char* request_json);
char* nocturne_alerts_references_wall_clock(const char* request_json);
char* nocturne_alerts_rule_reads(const char* request_json);
char* nocturne_alerts_references_any(const char* request_json);
char* nocturne_alerts_describe(const char* request_json);
char* nocturne_alerts_validate(const char* request_json);
char* nocturne_alerts_tracker_process(const char* request_json);
char* nocturne_alerts_tracker_force_close(const char* request_json);
char* nocturne_alerts_tracker_close_elapsed_hysteresis(const char* request_json);
char* nocturne_alerts_replay(const char* request_json);
void  nocturne_alerts_free_string(char* ptr);
```

- All strings are UTF-8, NUL-terminated. Every returned pointer is owned by
  the caller and must be released with `nocturne_alerts_free_string` exactly
  once (null is a no-op).
- `nocturne_alerts_version` returns the crate version (e.g. `0.1.0`) and
  `nocturne_alerts_tzdb_version` the IANA time zone database release compiled
  in (e.g. `2025b`), both plain strings, not JSON.
  `nocturne_alerts_clock_tolerance_ms` returns how far ahead of `now` an anchor
  may be and still read as `now` (`docs/alerts/engine-semantics.md` §5.4), a
  plain integer that is not freed. A host selects the newest reading for an
  alert context by it. `nocturne_alerts_hold_bound_ms` returns how long a rule
  may hold before its hold is reported (§6.4), likewise a plain integer: a host
  that evaluates on a timer evaluates a held rule once its hold has lasted that
  long. Everything else returns a JSON envelope.
- The library never panics across the boundary and never crashes on bad
  input: panics, null pointers, invalid UTF-8 and malformed JSON all come
  back as the error envelope:

```json
{ "schema_version": 1, "ok": false, "error": "human-readable message" }
```

## Evaluate envelope (`nocturne_alerts_evaluate`)

One call evaluates one rule for one tick: root, leaf log, excursion tracker,
then auto-resolve (`docs/alerts/engine-semantics.md` §7). The engine is stateless between
calls: **all** evaluation state (sustained timers, tracker) is carried in and
out as data, and the host persists it.

The `rule`, `context` and `result` shapes are exactly the golden-corpus
interchange shapes (`ScenarioRule`, `ScenarioContext`, `ExpectedRuleResult` in
`tests/Parity/Nocturne.Alerts.Corpus/ScenarioModels.cs`)
— the corpus in `tests/Parity/AlertEngineCorpus/` is the machine-checkable
spec for all of them. An enum value in the context (a state span's
`category`, `trend_bucket`, `glucose_bucket`, an active pump mode's `mode`,
`sensor_state.state`) is exactly its wire name, as in condition JSON; any other
spelling reads as absent, except a sensor state, which reads as a state that is
not ok (engine-semantics.md §4). `sensor_state` is
`{ "state": ..., "since": ..., "until": ... }` or absent: `state` is one of
`warming_up`, `ok`, `calibration_needed`, `error`, `expired`, `no_value`, `since`
(optional) is when it began, and `until` (optional) is when the host stops counting
the report. The engine reads a state whose `until` is at or before `now` as no
state, and reports `until` as a next change of every leaf that reads the fact. An
engine older than the member ignores it, as it ignores any context field it does
not know, so it evaluates such a context as one with no sensor state; a context
without it reads as no sensor state on every engine.

What a host sends (engine-semantics.md §4):

1. A reported state only from a report of the newest reading's own source: the
   report's device and data source are both present, the reading's are both
   present, and each equals the other. Anything else is no state.
2. A report counts while its timestamp is within the clock tolerance of `now` and
   it arrived at most 11 minutes before `now`, its arrival being the earlier of its
   timestamp and when the host stored it. `until` is that arrival plus 11 minutes.
3. For a newest reading with no glucose value: `latest_value`, `trend_rate`,
   `trend_bucket` and `glucose_bucket` absent.
4. `latest_timestamp` and `latest_received_at` that reading's own.
5. `last_reading_at` the last reading with a value (looked for up to 24 hours
   back, else the oldest reading looked at), since `signal_loss` and `staleness`
   mean no reading with a value.
6. `sensor_state` `no_value`, with `since` the first reading with no value after
   the last one with a value and no `until`.
7. A reported state that is not ok replaces `no_value`; a reported ok does not.

`predictions` is the `default` forecast series, the one array of `{offset_minutes, mgdl}` it has
always been. `prediction_curves` is an object from curve name (`iob`, `cob`, `uam`, `zt`) to such an
array; a `default` key there is not read, and a name the engine has no member for is dropped
(engine-semantics.md §4). `aps_determination` is `{ "at": ..., "eventual_mgdl": ...,
"carbs_required_grams": ... }` or absent: the loop's newest determination at or before `now`, each
value optional. A host sends `carbs_required_grams` `0` for an oref command without `carbsReq`, and
nothing for a Loop determination or a command it cannot read. An engine older than these members
ignores them, as it ignores any context field it does not know, so an unnamed `predicted` reads
`predictions` on every engine. `active_pump_modes` is the list of every active pump-mode span,
`[{ "mode": ..., "started_at": ... }]`: spans of different modes overlap, so a
host sends all of them, never the one it prefers, and a `pump_state` leaf reads
the entry for the mode it names (engine-semantics.md §3, §4). Timestamps are RFC 3339 UTC; the engine emits
whole-second instants without a fraction (`2026-01-05T12:00:00Z`) and
preserves sub-second precision when present.

### Request

```jsonc
{
  "schema_version": 1,                      // required; only 1 is accepted
  "rule": {                                 // ScenarioRule corpus shape
    "id": "00000000-0000-0000-0000-000000000001",
    "condition_type": "sustained",          // canonical wire string
    "condition_params": { /* payload */ },  // payload object as stored
    "hysteresis_minutes": 0,                // default 0
    "auto_resolve_enabled": false,          // default false
    "auto_resolve_params": { "type": "…" }, // full ConditionNode or null
    "on_unknown": "hold",                   // hold|met|not_met; default hold; any other word is an envelope error
    "glucose_freshness_minutes": 11         // a whole number from 1 to 1440; absent or null is 11 (semantics §3); any other value is an envelope error
  },
  "context": { /* ScenarioContext corpus shape */ },
  "now": "2026-01-05T12:00:00Z",            // the evaluation instant
  "timers": {                               // optional; default {}
    "sustained": "2026-01-05T11:55:00Z"     // condition path -> first-true instant
  },
  "not_true": {                             // optional; default {}
    "composite[1].sustained": "2026-01-05T11:55:00Z" // condition path -> last not-true instant
  },
  "tracker": {                              // optional; absent = never evaluated
    "state": "active",                      // idle|active|hysteresis; absent = no per-rule state yet; any other string, a stored "confirming" included, reads as active with an excursion, else idle (semantics §6)
    "active_excursion_ordinal": 3,          // present only while an excursion is active
    "updated_at": "2026-01-05T11:55:00Z",   // REQUIRED whenever state is present
    "hysteresis_started_at": null,          // set only in hysteresis; absent there = adopt updated_at once
    "awaiting_rearm": true,                 // present only when true; absent = armed (§6.3)
    "last_excursion_ordinal": 3,            // the rule's last opened excursion; absent = the active ordinal, or 0
    "held_since": "2026-01-05T11:00:00Z"    // present only while held (§6.4); absent = not held, so a hold counts from the next held evaluation
  },
  "include_leaves": true                    // optional; default false
}
```

`include_leaves: true` adds the leaf log, `result.leaves`: every leaf of the
body evaluated alone, which costs an evaluation per leaf on every call. It is
off unless asked for, and nothing else in the response depends on it. A host
asks for it only where it shows the leaf log (a corpus harness, a per-leaf
readout); live evaluation does not.

Unknown fields (e.g. the scenario `name`) are ignored, so a corpus
`ScenarioRule` object can be passed as `rule` verbatim.

### Response

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "result": { /* ExpectedRuleResult corpus shape:
                 rule_id, skipped?, root, unknown?, unknown_facts?,
                 leaves[{leaf_id, value, unknown?}], transition,
                 close_reason?, held_too_long?,
                 tracker {state, excursion?,
                          hysteresis_started_at?, awaiting_rearm?,
                          held_since?},
                 auto_resolved?, timer_ops[], next_change_at?,
                 clock_anomaly?, met_by? */ },
  "timers": { "sustained": "2026-01-05T12:00:00Z" },  // full post-state; persist verbatim
  "not_true": {},                                     // full post-state; persist verbatim
  "tracker": {                                        // full post-state; persist verbatim
    "state": "active",
    "active_excursion_ordinal": 3,
    "updated_at": "2026-01-05T12:00:00Z",
    "hysteresis_started_at": "…",                     // present only while in hysteresis
    "awaiting_rearm": true,                            // present only when true
    "last_excursion_ordinal": 3,                       // present once the rule has opened one
    "held_since": "…"                                  // present only while held
  }
}
```

A root or leaf that is unknown (`docs/alerts/engine-semantics.md` §2.4) reads
`false` with `"unknown": true` beside it; the flag is absent otherwise, so a
reader that ignores it sees a two-valued result. An unknown root also carries
`unknown_facts`, `[{"fact": "glucose", "reason": "stale"}]`: the context facts
(§5.2) whose unknown reached the root through the connectives, each with its
reason (`absent`, `stale`, `ahead_of_clock`, `unrecognised`, `inexact`,
`sensor_not_ok`), in fact then reason order. A `predicted` leaf whose selected curve the context
does not carry is unknown by `predictions`, `absent`; a stale determination by
`aps_determination`, `stale`. A glucose leaf left unknown by the
sensor's state is named by `sensor_state`, with `sensor_not_ok`, or
`unrecognised` for a state the engine has no name for. It can be empty: a `sustained` run whose start is beyond the
clock tolerance ahead names no fact.

The rule's `on_unknown` decides what an unknown root does to the tracker (§6.4).
Under `hold` the tracker is not advanced: `transition` is `none` unless a
hysteresis window has elapsed, and the returned `tracker` is the one sent with
`held_since` set from the first held evaluation, a rule never evaluated before
then holding an idle state with only that. The evaluation at which a hold has
lasted `nocturne_alerts_hold_bound_ms` (24 hours) carries `held_too_long: true`,
once per hold; from it an open excursion reads its root as false, so it enters
hysteresis and closes with `close_reason` `hold-bound`. Any evaluation whose root
is known, or read as known, clears `held_since`.

Envelope-level errors (`ok: false`) are reserved for unusable requests: malformed JSON, wrong
`schema_version`, unknown root `condition_type`, an `on_unknown` naming no choice, a
`glucose_freshness_minutes` that is not a whole number from 1 to 1440, a tracker `state` without
`updated_at`, an instant (`now`, a timer, a tracker or context timestamp)
before 0001-01-01 or from 10000-01-01 UTC on, and a rule body that cannot be evaluated — malformed anywhere in the tree, or one of the shapes in
`docs/alerts/engine-semantics.md` §1.4. That last error reads
`malformed condition_params for '<type>': <reason> at '<path>'`; the host
skips the rule and keeps its timers and tracker unchanged. An auto-resolve
tree that cannot be evaluated never resolves and is not an error.

`result.skipped` belongs to the corpus shape; since a body that cannot be
evaluated is an error envelope, this envelope never sets it.

### Clear margin

A comparison leaf may carry `clear_margin` in its payload (`docs/alerts/engine-semantics.md` §3,
clear margin): while the rule's excursion is open, the leaf compares against its limit moved by the
margin toward the side that keeps the rule true. It adds no request field: the engine reads
"open" from the `tracker` the request already sends (`active` or `hysteresis` with an
excursion), so a request without a tracker reads every leaf closed, as `evaluate_node` always
does. It is an additive member of the stored condition, and `schema_version` stays 1. An engine
from before it reads the member as an unknown field (§1.4) and returns the malformed-body error
(`unknown_field`), so a host running that engine skips the rule rather than evaluating it without
its margin. New reason codes: `margin_negative`, `margin_on_equality`, `margin_past_zero` (a
`rate_of_change` margin above its `rate`) and `margin_under_sustained` (a margin above 0 on a leaf
anywhere under a `sustained`), each on the field `clear_margin`.

### Forecast curves, the loop's determination and what met a leaf

`predicted` takes at most one of `curve` (a name), `min_of` and `max_of` (a list of names) to select
the series it reads; none reads `default`, exactly as before (`docs/alerts/engine-semantics.md` §3,
forecast curves). `eventual_glucose` and `carbs_required` are leaves of their own over the context's
`aps_determination`. New reason code: `selector_conflict`, on the second selector present. An
engine from before them reads a selector as an `unknown_field` and either leaf as an
`unknown_kind`, and returns the malformed-body error, so a host running it skips the rule.

`result.met_by` lists what each true `predicted`, `eventual_glucose` and `carbs_required` leaf of
the body met its comparison with, by leaf id. It is absent when there is none, and it does not
depend on `include_leaves`:

```jsonc
"met_by": [
  { "leaf_id": 0, "kind": "predicted", "curve": "cob", "offset_minutes": 20, "value": 65 },
  { "leaf_id": 1, "kind": "carbs_required", "value": 12 }
]
```

The point is the first, in offset order, that satisfied the comparison, from the curve that
supplied the lowest or highest value for `min_of`/`max_of`. `value` is mg/dL, or grams for
`carbs_required`. Additive, so `schema_version` stays 1.

### A limit from a fact

`threshold`, `predicted` and `eventual_glucose` take exactly one of `value` and
`value_from: {"fact": "latest_glucose" | "target_low" | "target_high", "offset"?: <mg/dL>}`
(`docs/alerts/engine-semantics.md` §3, a limit from a fact). The context gains `target_low` and
`target_high` (mg/dL, optional), the ends of the profile's target range at `now`; an absent one
leaves a leaf that names it unknown by `target_range`, reason `absent`. New reason codes:
`limit_conflict` (both, on `value_from`), `offset_out_of_range` (more than 1000 either side of 0, on
`offset`) and `self_reference` (a `threshold` against `latest_glucose`, on `value_from`); neither
member is `field_missing` on `value`. A true `threshold` whose limit names a fact joins `met_by`,
and every entry whose limit names a fact carries `limit` (the effective limit compared against),
`limit_fact` and `limit_offset` (the rule's offset to the fact, absent when 0):

```jsonc
{ "leaf_id": 0, "kind": "threshold", "value": 66, "limit": 70, "limit_fact": "target_low", "limit_offset": -10 }
```

An engine from before it reads `value_from` as an `unknown_field` and returns the malformed-body
error, so a host running it skips the rule; it ignores `target_low` and `target_high` in the
context. A rule with `value` reads as it always has. Additive, so `schema_version` stays 1.

### Next change

`result.next_change_at` (RFC 3339, on the 100 ns tick grid) is the earliest
instant strictly after `now` at which an evaluation from the returned `timers`,
`not_true` and `tracker` can do anything other than repeat this result's `root` and
`leaves` with no transition but `continues` and no auto-resolve, if the context
stays as sent and only `now` advances (`docs/alerts/engine-semantics.md` §5.3).
It is absent when no such instant exists, or when it would fall after
9999-12-31T23:59:59.9999999Z. It is a lower bound: re-evaluating at
it may find nothing changed, and a host then reads the next one from that
evaluation. A host may evaluate a wall-clock rule at this instant in place of a
periodic timer only while nothing but `now` moves the facts the rule reads
(§5.1); a write that moves one must evaluate the rule. Additive, so
`schema_version` stays 1. The tracker entry points do not report it: a rule's
next change needs its full evaluation.

### Clock anomaly

`result.clock_anomaly` lists every anchor or state instant the evaluation read
more than 5 minutes ahead of `now`, which the engine reads as not known
(`docs/alerts/engine-semantics.md` §5.4). It is absent when there is none.

```jsonc
"clock_anomaly": [
  { "fact": "last_reading_at", "ahead_ms": 3600000 }  // ClockAnchor order, once each
]
```

`fact` is the context field the instant came from (`last_reading_at`,
`latest_timestamp`, `last_aps_cycle_at`, `last_aps_enacted_at`,
`last_site_change_at`, `last_sensor_start_at`, `last_carb_at`, `last_bolus_at`,
`active_alerts`, `active_pump_suspension`, `active_override`,
`active_do_not_disturb`, `active_pump_modes`, `active_state_spans`,
`active_trackers`, `sensor_state`, `aps_determination`), the rule's `enabled_at`, or the evaluation state that held
it (`sustained` for a timer, `hysteresis_started_at` for the tracker).
`ahead_ms` is how far ahead of `now` it was, in whole milliseconds, the furthest
when two instants share a name. Under `hold` such a rule is held, so a host
tells someone a device clock is ahead while it is reported. A host also selects
the newest reading for an alert context by the tolerance, so a reading dated
beyond it never becomes `last_reading_at` (engine-semantics.md §4). Additive,
so `schema_version` stays 1.

### State threading

- **`timers`** and **`not_true`** are per-rule: key is the condition path of
  the `sustained` node; a path is in `timers` with the instant its child's run
  started, or in `not_true` with a recorded evaluation that found its child
  false; no later run starts before the earlier of that instant plus 5
  minutes (the not-true lease, engine-semantics.md §3) and `now`. A path is in
  neither when nothing is recorded for it. Persist both response objects
  and send them back on the rule's next evaluation. (They are keyed by path
  only — the rule id is implicit in the call.) A host that persisted `timers`
  alone before `not_true` existed sends `not_true` absent: a path in neither
  starts its next run at `now`, as before, unless the rule has no state at all
  (no `timers`, no `not_true`, no tracker `state`), when the rule's
  `enabled_at` bounds it. A host that stores runs where an older version of
  itself reads them keeps `not_true` apart, so the older version never reads a
  not-true path as a run.
- **Raise stored not-true instants.** Before sending `not_true`, raise every
  instant in it to at least the instant your process began recording them
  without interruption, and keep the path. A stored instant bounds a start
  only because every false evaluation after it was recorded; one written before
  an interruption (an older version that did not record them, writes lost
  before a restart) can be older than evaluations that found the child false,
  and a start taken from it could go back over them. Do not drop the path
  instead: a path with no state reads as never found false, and a rule with no
  state at all as on its first evaluation (engine-semantics.md §3, §7).
- **`timer_ops`** are the same changes as deltas, for a host that writes rows:
  `set` puts the path in `timers` at `at`, `clear` puts it in `not_true` at
  `at` (and takes it out of `timers`). Every op carries `at`. A `clear` comes
  when a run ends, on a path's first false evaluation, and on a false
  evaluation 5 minutes or more after the path's recorded not-true instant;
  a false evaluation inside that lease records nothing.
- **`tracker`** fields (`state`,
  `active_excursion_ordinal`, `updated_at`, `hysteresis_started_at`,
  `awaiting_rearm`, `last_excursion_ordinal`, `held_since`) are the rule's
  alone: persist the response object and send it back on the rule's next
  evaluation. They are absent until the rule's first non-skipped evaluation.
  Dropping `held_since` starts the hold over, so its bound comes later. The
  evaluation that reaches the bound sets `updated_at`, which is what keeps a
  later evaluation of the same hold from reporting it again.
  `hysteresis_started_at` is what hysteresis expiry measures
  from; dropping it makes every restore adopt `updated_at`, which slides the
  window forward on each evaluation. `awaiting_rearm` holds an auto-resolved
  rule off while both its condition and its auto-resolve tree hold; dropping
  it lets the next evaluation re-open and re-dispatch.
- **Excursion identity is per rule.** A rule numbers its excursions 1, 2, ...
  in the order it opens them; `last_excursion_ordinal` is the last it opened
  and `active_excursion_ordinal` the one open now. An excursion is identified
  by its rule id and that ordinal, and nothing is threaded between rules.
  Dropping `last_excursion_ordinal` restarts the numbering from the active
  ordinal (or 1), which reuses an ordinal an earlier, closed excursion of the
  rule had: a host that keys work by the ordinal persists it. A host that
  keys excursions by ids of its own may send only whether one is active, and
  map `result.transition` to its ids.
- **Migrating stored state.** A host that stored tracker state with the
  tenant-wide `next_excursion_ordinal` MUST seed each rule's
  `last_excursion_ordinal` with that stored `next_excursion_ordinal` minus 1.
  The tenant-wide counter was at least every rule's highest ordinal, so no
  rule's next excursion reuses an ordinal it already had. Without the seed
  the next excursion restarts at the active ordinal plus 1, or 1, and a host
  that dedupes work by `(rule, ordinal, kind)` against its history would drop
  that new alert as a duplicate.

## Acting on transitions

An `opened` or `closed` transition is work the host owes: for an opening, the
alert itself (its record, its delivery on each channel, Do Not Disturb, an
info rule's acknowledgement); for a close, its resolution. Hosts MUST act on
transitions in this order, so that a process that dies at any point loses no
alert (`docs/alerts/engine-semantics.md` §7):

1. **Persist the state and the transition together.** Write the returned
   `timers`, `not_true` and `tracker`, and a record that the transition's
   work is owed, in one transaction. The record is identified by
   `(rule, excursion, kind)` (kind `opened` or `closed`); writing it twice
   is a no-op.
2. **Act on it.** Every step MUST be safe to run again: act on a
   `(rule, excursion, kind)` at most once per channel, skip a channel the
   record shows delivered or refused, and send one it shows no answer for.
3. **Mark it done**, after the last step.
4. **On start, and periodically after,** act on every record still owed
   whose last attempt is older than a lease (the backend uses 2 minutes),
   taking it up first so that two workers do not both act on it.

Delivery is therefore at least once, never at most once: a duplicate alert
is acceptable, a lost one is not. Where a step cannot tell whether it
completed, it runs again. An owed `opened` is marked done without sending in
exactly two cases, since an alert for a condition that has ended misleads:
its excursion has closed, or its rule is disabled or deleted. An
acknowledgement is not a cutoff: an acknowledge-all made for another alarm
must not consume an alert nobody received. A rule the host cannot read keeps
its `opened` owed until one of the two cases applies. Check the close again
where the opening's record is written, under the lock its close's
resolution also takes, so a close that came first stops the opening. A close
that lands while the opening is being sent can still leave its alert
standing. An owed `closed` is
always finished. A late alert is built from the context at the time it is
sent and carries that reading's time, not the opening's.

## Evaluate node (`nocturne_alerts_evaluate_node`)

Evaluates a single condition tree for one instant **outside** the per-rule
driver: no tracker, no auto-resolve, no leaf log — just the node's truth plus
sustained-timer state threading. It serves the auxiliary evaluation scopes a
host runs against reserved path roots:
smart-snooze conditions (`root: "snooze"`) and the sweep's periodic
auto-resolve (`root: "auto_resolve"`).

### Request

```jsonc
{
  "schema_version": 1,                      // required; only 1 is accepted
  "rule_id": "00000000-0000-0000-0000-000000000001", // keys sustained timers
  "node": { "type": "composite", "composite": { /* … */ } }, // full ConditionNode
  "root": "snooze",                         // optional root path segment;
                                            // defaults to the node's type
  "context": { /* ScenarioContext corpus shape */ },
  "now": "2026-01-05T12:00:00Z",
  "timers": {                               // optional; default {}
    "snooze": "2026-01-05T11:55:00Z"        // condition path -> first-true instant
  },
  "not_true": {}                            // optional; default {}; as in evaluate
}
```

### Response

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "value": true,                            // the node's truth; false when unknown
  "unknown": true,                          // present only when the node is unknown
  "timers": { "snooze": "2026-01-05T12:00:00Z" },  // full post-state; persist verbatim
  "not_true": {},                                  // full post-state; persist verbatim
  "timer_ops": [                            // observable mutations, execution order
    { "op": "set", "path": "snooze", "at": "2026-01-05T12:00:00Z" }
  ],
  "clock_anomaly": [                        // present only when an instant was read too far ahead
    { "fact": "last_reading_at", "ahead_ms": 3600000 }
  ]
}
```

`clock_anomaly` is as in `evaluate` (see Clock anomaly above).

A `node` that cannot be evaluated (`docs/alerts/engine-semantics.md` §1.4,
with paths under `root`) is an envelope error (`ok: false`) naming every
problem, which a host treats as `false`. Timers are keyed by
the same `(rule_id, path)` identity as `evaluate`; sharing rows between the
per-reading and sweep variants of a scope is intentional (see
`docs/alerts/engine-semantics.md` §2.3).

## Leaf paths (`nocturne_alerts_leaf_paths`)

For hosts that prune stale timers: given a condition tree, returns the
condition path of every node and each leaf's path by leaf id
(`docs/alerts/engine-semantics.md` §2.2–2.3). A tree that cannot be evaluated
is an envelope error.

Input is either a full condition node, or a wrapper naming the root path
segment (default: the node's `type`; pass `"auto_resolve"` for auto-resolve
trees):

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

Pruning timers to the `paths` set is always safe — it is every path a timer
can be keyed under for that tree.

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

Whether a rule must be evaluated on a timer as well as per reading: one of its
kinds measures elapsed time against an anchor a reading does not move
(`docs/alerts/engine-semantics.md` §5.1). A host that evaluates only when a
reading arrives never fires such a rule while readings stop, so it schedules
these rules on its sweep. A rule whose `on_unknown` is `met` or `not_met` is
wall-clock too when it has a `threshold`, `rate_of_change`, `trend` or
`glucose_bucket` kind: that leaf turns unknown when its reading goes stale
(semantics §3). The request is the rule body, as for `classify`, plus the rule's
auto-resolve configuration and `on_unknown`, all optional:

```jsonc
{
  "schema_version": 1,
  "condition_type": "composite",
  "condition_params": { "operator": "and", "conditions": [ /* … */ ] },
  "auto_resolve_enabled": true,                     // default false
  "auto_resolve_params": { "type": "staleness", … }, // a full node; default null
  "on_unknown": "hold"                               // hold|met|not_met; default hold
}
```

Response:

```jsonc
{ "schema_version": 1, "ok": true, "references_wall_clock": true }
```

A rule's kinds are read as evaluation reads them:

- `condition_type` is exactly a kind's wire name. Anything else gives the rule
  no kinds, so the answer is `false`, not an error.
- The root kind counts whatever its body, so a wall-clock root is swept and
  its `evaluate` rejects the body as it would per reading.
- The body's kinds count only when it can be evaluated, a nested `type`
  resolving as node dispatch resolves it (semantics §1.2).
- The auto-resolve tree's kinds count only while `auto_resolve_enabled`, and
  only when the tree is present, not `null` and evaluable.

Only a malformed envelope, or an `on_unknown` naming no choice, is the error
envelope.

## Rule reads (`nocturne_alerts_rule_reads`)

The context facts a rule reads (`docs/alerts/engine-semantics.md` §5.2): the
facts of every kind of the rule, read as for `references_wall_clock`, which
takes the same request. Facts are named by their snake_case wire names, once
each, in declaration order:

```jsonc
{ "schema_version": 1, "ok": true, "reads": ["glucose", "carbs"] }
```

The names are `glucose`, `predictions`, `insulin`, `carbs`, `pump`, `aps`,
`uploader`, `transmitter`, `state_spans`, `device_ages`, `trackers`, `sleep`,
`active_alerts`, `do_not_disturb`, `time_zone`, `sensor_state` and `aps_determination`. A rule with no kinds reads
`[]`.

## References any (`nocturne_alerts_references_any`)

Whether a rule reads any of `facts`. The request is the `rule_reads` request
plus a required `facts` list of wire names, matched exactly:

```jsonc
{ "schema_version": 1, "condition_type": "threshold", "condition_params": { … }, "facts": ["carbs"] }
```

```jsonc
{ "schema_version": 1, "ok": true, "references_any": false }
```

A missing `facts` list or a name this library does not know is the error
envelope: ignoring an unknown name could skip a rule the host meant to
evaluate.

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
    "operator": "and",                       // and | or
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
- **Leaves** carry `leaf_id`, `type` and `kind` (both the kind's wire name),
  and decoded `params`.
- **Leaf ids match `evaluate` exactly.** Both number the same node's leaves in
  the same pre-order walk.
- **Operands are rendered as read:** enum operands by wire name; decimal
  operands round-trip **exactly** (the crate's `serde_json` uses
  `arbitrary_precision`).
- Like `evaluate`, an **unknown `condition_type`** (or a malformed envelope /
  wrong `schema_version`) is an error envelope. A body that cannot be evaluated
  is not: its `tree` is `null`.
- **Auto-resolve tree.** A request may also carry `auto_resolve_params`, a full
  condition node as a rule stores it. The response then has
  `auto_resolve_tree`, described the same way with paths under the
  `auto_resolve` root and leaf ids numbered from 0, as `evaluate_node` numbers
  them for that root. It is `null` when the tree is `null` or cannot be
  evaluated, since evaluation never reads such a tree. Without
  `auto_resolve_params` the field is absent. The backend reads the leaves of
  both trees to decide what context to fetch and which rules an `alert_state`
  reference ties together.

## Validate (`nocturne_alerts_validate`)

The save-time check of a rule's condition trees: every problem that keeps a
tree from being evaluated (`docs/alerts/engine-semantics.md` §1.4). Hosts call
it before storing a rule and refuse the save when `valid` is false; the backend
does so in `AlertRulesController` (a 400), and shows a stored rule's problems
in the rule editor.

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
on, when there is one. Neither ever carries a payload value. Issues are listed
in pre-order. A rule with issues is `ok: true`; only an unusable envelope is the
error envelope.

## Tracker (`nocturne_alerts_tracker_*`)

The excursion state machine (`docs/alerts/engine-semantics.md` §6) without a
condition tree, for the host paths that move a tracker without evaluating a
rule: feeding an externally decided truth, closing an excursion by hand or on
auto-resolve, and the periodic close of hysteresis windows no evaluation
arrives to close. The `tracker` object in and out is the evaluate envelope's,
threaded the same way (see "State threading").

Requests (all take `schema_version` and `now`; `tracker` absent or null means
the rule has never been evaluated):

```jsonc
// nocturne_alerts_tracker_process: one evaluation's truth
{ "schema_version": 1, "tracker": { /* … */ }, "now": "…",
  "config": { "hysteresis_minutes": 0 },  // optional
  "condition_met": true,
  "auto_resolve_met": true }                 // optional, default false; see below

// nocturne_alerts_tracker_force_close: from any state holding an excursion (§6.2)
{ "schema_version": 1, "tracker": { /* … */ }, "now": "…",
  "reason": "manual" }                       // hysteresis | auto | manual | rule-disabled | hold-bound

// nocturne_alerts_tracker_close_elapsed_hysteresis: only in hysteresis, only once the window has elapsed (§6.1)
{ "schema_version": 1, "tracker": { /* … */ }, "now": "…",
  "config": { "hysteresis_minutes": 30 } }
```

`condition_met` is a known truth, so `process` clears `held_since`: an
evaluation whose root the rule holds (`on_unknown` `hold`, §6.4) goes through
`nocturne_alerts_evaluate`, not this call. A close of an elapsed window while
held leaves `updated_at` as it is, and closes with `hold-bound` when the
hysteresis began during the hold. `auto_resolve_met` is the rule's auto-resolve tree
this evaluation, true unless the tree is known false (an unknown tree keeps the
hold), and is read only while the tracker awaits re-arm (§6.3): the rule then
stays held off when both it and `condition_met` are true, and otherwise re-arms and goes on through
the state machine in the same call, so a `condition_met` of true opens at once. Evaluate the tree at the `auto_resolve` root with
`nocturne_alerts_evaluate_node` before this call when the stored tracker has
`awaiting_rearm`; a rule without an enabled, evaluable tree sends false.

Response:

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "transition": { "type": "closed", "excursion_ordinal": 3, "close_reason": "manual" },  // the rule's ordinal
  "tracker": { /* full post-state; persist verbatim */ }
}
```

`transition.type` is the evaluate result's `transition` vocabulary;
`excursion_ordinal` names the excursion the transition involved, which a close
has already cleared from `tracker`. A hysteresis state without
`hysteresis_started_at` adopts `updated_at` on the close-elapsed call even when
the window has not elapsed, so persist the returned `tracker` either way. An
unknown `reason` is the error envelope.

## Replay (`nocturne_alerts_replay`)

Steps a whole rule set over a series of ticks in one call
(`docs/alerts/engine-semantics.md` §8): what the rules would have done over a
historical window, through the same driver sequence `evaluate` runs. The host
builds each tick's context as of that instant; the engine owns everything else
for the call: one fresh timer store, one fresh excursion tracker, and the
active alerts `alert_state` reads. Nothing carries over between calls and
nothing is persisted.

Request:

```jsonc
{
  "schema_version": 1,
  "rules": [ /* ScenarioRule corpus shape, as for evaluate; any order */ ],
  "ticks": [
    {
      "at": "2026-01-05T12:00:00Z",         // the tick instant ("now")
      "context": { /* ScenarioContext corpus shape */ },
      "suppressed_rule_ids": [ "…" ],        // optional; see below
      "reading": true                        // optional; default true; false is a sample between readings
    }
  ],
  "include_ticks": false,                   // optional; default false
  "until": "2026-01-06T00:00:00Z"           // optional; the window's end; see "Steps"
}
```

- **Order.** Rules are evaluated so that every rule runs after the rules its
  `alert_state` leaves reference; a reference cycle keeps the given order.
- **Ticks.** A `reading` tick evaluates every rule, as live evaluates every
  rule per reading. A sample (`"reading": false`) is a sweep: each elapsed
  hysteresis window closes, then the wall-clock rules and the rules that read
  a fact other than glucose and the time zone are evaluated.
- **Steps.** Between ticks the engine evaluates each wall-clock rule at its
  `next_change_at`, against the latest tick's context with only `now`
  advanced, and closes another rule's hysteresis window where it ends. A step
  closes the due rule's elapsed window before it evaluates. With `until` it
  steps on past the last tick up to, not including, that instant. A rule due
  more than 1,000 times between two ticks is an error.
- **Active alerts.** A tick context's `active_alerts` is replaced by the
  replay's own: a rule is in it, `firing` from the instant an excursion
  opened, until the excursion closes. An opening is visible to rules later in
  the order at the same instant.
- **No reading.** A context with neither `latest_timestamp` nor
  `last_reading_at` reads `last_reading_at` as the tick instant, so staleness
  and `signal_loss` see a fresh reading rather than none. Send the last
  reading at or before the tick when there is one.
- **Suppression.** `suppressed_rule_ids` names the rules an excursion opening
  on that tick, or on a step before the next tick, is recorded for as
  `suppressed_by_dnd` instead of `fired` (the host resolves Do Not Disturb).
  The excursion opens either way.
- A rule whose body cannot be evaluated (§1.4), or whose `condition_params`
  is neither an object nor null, is skipped on every tick, not an error.

Response:

```jsonc
{
  "schema_version": 1,
  "ok": true,
  "order": [ "…" ],                         // rule ids in evaluation order
  "events": [                               // by instant, then evaluation order
    { "at": "2026-01-05T12:00:00Z", "rule_id": "…", "kind": "fired" }
  ],
  "leaf_transitions": [                     // evaluation order; absent for a rule never evaluated
    { "rule_id": "…", "leaves": [
        { "leaf_id": 0, "points": [ { "at_ms": 1767614400000, "value": true } ] } ] }  // a point's "unknown": true beside a false value marks an unknown leaf
  ],
  "ticks": [                                // only with include_ticks: every evaluation instant
    { "at": "2026-01-05T12:00:00Z", "rules": [
        { "rule_id": "…", "met": true, "firing": true },  // "unknown": true beside a false met marks an unknown root
        { "rule_id": "…", "skipped": true } ] }
  ]
}
```

`kind` is the tracker's transition: `fired` or `suppressed_by_dnd` (an
excursion opened), `hysteresis_started`, `hysteresis_resumed`, `closed` (its
hysteresis window ran out) or `auto_resolved`. An instant between ticks is on
the engine's 100 ns grid, so it can carry a fraction in nanoseconds
(`12:20:00.000000100Z`). The leaf log holds, per leaf id, the first observation
and every flip, at unix milliseconds, so a flip at a tick and one at a step
within the same millisecond share an `at_ms`. A tick entry lists the rules evaluated at that instant; `firing`
is whether an excursion is open after it. Errors are an unusable envelope, an
unknown `condition_type`, a rule id listed twice, a tick instant or `until`
outside the supported range, or a rule past the step bound. The replay corpus
in `tests/Parity/AlertEngineCorpus/replay/` pins this envelope.

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
fun ruleReads(requestJson: String): String            // nocturne_alerts_rule_reads
fun referencesAny(requestJson: String): String        // nocturne_alerts_references_any
fun leafPaths(requestJson: String): String            // nocturne_alerts_leaf_paths
fun describe(requestJson: String): String             // nocturne_alerts_describe
fun validate(requestJson: String): String             // nocturne_alerts_validate
fun trackerProcess(requestJson: String): String       // nocturne_alerts_tracker_process
fun trackerForceClose(requestJson: String): String    // nocturne_alerts_tracker_force_close
fun trackerCloseElapsedHysteresis(requestJson: String): String // nocturne_alerts_tracker_close_elapsed_hysteresis
fun replay(requestJson: String): String               // nocturne_alerts_replay
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

## Swift (UniFFI)

The same `uniffi` surface generates Swift bindings, so an iOS host (Trio, Loop)
consumes the engine as a Swift package with the envelope contract of the Kotlin
section. The functions keep their Kotlin names and shapes, as Swift free
functions with labelled arguments:

```swift
func evaluate(requestJson: String) -> String
func evaluateNode(requestJson: String) -> String
func replay(requestJson: String) -> String
func version() -> String
// ... one per Kotlin function above; the module is `NocturneAlerts`
```

Thread `timers`, `not_true` and `tracker` from each response into the next request as
described in "State threading". The package test replays a corpus scenario that
way.

### Building the package

macOS only (it needs `xcodebuild`, `lipo` and the Apple SDKs). From the repository root:

```bash
crates/nocturne-alerts-ffi/scripts/build-xcframework.sh
(cd crates/nocturne-alerts-ffi/swift && swift test)
```

The script adds the Rust targets, builds the static library with
`--features uniffi` for iOS (device, and simulator on arm64 and x86_64), macOS
(arm64 and x86_64), generates the Swift source with the in-crate
`uniffi-bindgen` in library mode from the macOS arm64 library (the bindings
depend on the crate revision and features, which every slice shares), and
assembles `crates/target/swift/NocturneAlertsFFI.xcframework` with
`xcodebuild -create-xcframework`. It then copies the xcframework and the
generated `nocturne_alerts.swift` into `swift/`, the package that
`swift test` builds.

The static library is built with `cargo rustc --crate-type staticlib` for the
iOS targets, so the cdylib the .NET build packs is not built for
them. The crate already lists `staticlib` in `crate-type`; the macOS targets use
a plain `cargo build`, which also yields the `.dylib` the bindings are read from.

The xcframework and the generated Swift are build outputs and are not
committed. The job `alerts-swift` in `.github/workflows/tests.yml` runs
the script and `swift test` on macOS when `crates/`, the corpus or the workflow
changes. It also links the test bundle against the iOS Simulator slice, and
compiles the package against the iOS device slice without linking it. Nothing
runs the XCTest in a simulator.

### Adding the package to a host

CI or the host's build runs the script, then depends on the package by path:

```swift
.package(path: "path/to/crates/nocturne-alerts-ffi/swift")
```

and adds the `NocturneAlerts` product to its target. The bindings checksum the
API at load time and refuse a mismatched library, so the Swift source and the
xcframework must come from the same crate revision and the same uniffi version
(pinned exactly in this crate's `Cargo.toml`). Do not mix the output of two builds.

### Not supported

- Publishing: there is no released xcframework or hosted Swift package; a host
  builds it from a checkout.
- Supported slices: iOS device (arm64), iOS Simulator (arm64 and x86_64) and
  macOS (arm64 and x86_64). The package declares iOS 15 and macOS 13.
- watchOS: not built. The pinned toolchain ships a prebuilt standard library
  for `aarch64-apple-watchos` only, and Apple Watch models before Series 9 and
  Ultra 2 need `arm64_32-apple-watchos`, which has none (it needs nightly
  `-Zbuild-std`). A half-supported watch slice is left out.
- tvOS, visionOS and Mac Catalyst.
- Linux Swift: the package links the Apple xcframework only.

## Browser (WebAssembly)

The optional `wasm` cargo feature adds a [wasm-bindgen](https://rustwasm.github.io/docs/wasm-bindgen/)
surface (`src/wasm_api.rs`) for the docs portal's alert simulator, which runs
this engine against fixture traces instead of a copy of its semantics. It
exports `replay`, `validate` and `version`, with the same envelopes and
handlers as their C counterparts:

```js
import init, { replay, validate } from "./nocturne_alerts.js";
await init();
const outcome = JSON.parse(replay(JSON.stringify(request))); // nocturne_alerts_replay
```

wasm32 has no unwinding, so a panic traps and reaches the host as a thrown
`RuntimeError` rather than the error envelope. wasm-bindgen is pinned exactly:
the CLI that generates the JS glue must be the same version as the crate.
`src/Web/packages/portal/scripts/build-alerts-engine.mjs` builds it, installing
the matching CLI into `crates/target/tools/`:

```bash
# from crates/
cargo build --release --target wasm32-unknown-unknown -p nocturne-alerts-ffi --features wasm
```

## Versioning

`schema_version` covers the envelope layer. The time zone rules `time_of_day`
and `day_of_week` apply are the IANA release compiled into the library
(`chrono-tz`), not the host's; `nocturne_alerts_tzdb_version` reports it, so a
host can log it at load and notice when it falls behind a zone change. Keeping
it current means updating the `chrono-tz` dependency and rebuilding. Behavioural changes to evaluation
itself are governed by the golden corpus: the Rust core (`tests/parity.rs`),
this crate's FFI round-trip test, and the .NET FFI parity suite
(`tests/Unit/Nocturne.Alerts.Native.Tests`) pin every scenario against the
committed `.expected.json` snapshots.
