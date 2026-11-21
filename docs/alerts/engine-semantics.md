# Alert Engine Semantics (Normative)

This document specifies the observable behaviour of the leaf/node alert evaluation engine,
the shared Rust crate `nocturne-alerts-core` that the backend (over `nocturne-alerts-ffi`) and
Prelude (over UniFFI) both run. Every statement here is **normative**, including behaviours
that look like bugs; those are marked **[anomaly]**, and changing one is a deliberate change
to this document, the crate and the corpus together.

The behaviour was first pinned from the C# engine the crate replaced, so some sections still
explain a rule by the C# semantics it keeps (`decimal` arithmetic, `TimeSpan` math). That
engine no longer exists.

The machine-checkable form of this document is the golden corpus in
`tests/Parity/AlertEngineCorpus/`. It is frozen: a committed `.expected.json` is never
regenerated. A new scenario's snapshot is written from the engine with
`NOCTURNE_CORPUS_BLESS=1 cargo test -p nocturne-alerts-core --test parity` (or `--test
replay_parity`) and reviewed like code.

---

## 1. Wire format

### 1.1 JSON options

All condition JSON (rule `condition_params`, `auto_resolve_params`, snooze `conditions`)
has exactly one spelling for every property name and enum value. Anything else is not read:

- property names are **snake_case lower** (`JsonNamingPolicy.SnakeCaseLower`), matched
  **exactly**. A property in another case is a different property, so the tree has an
  unknown property and is missing the operand it was meant to be (§1.4)
- a property name that repeats binds its **last** occurrence
- an enum value is a JSON string that is **exactly** one member's wire name. Another case,
  surrounding whitespace, or an integer (ordinal) names no member
- condition-only enums use snake_case wire names (`TempBasalMetric`: `rate` |
  `percent_of_scheduled`; `TransmitterBatteryMetric`: `voltage_a` | `voltage_b` |
  `resistance` | `days`; `GlucoseBucket`: `very_low` | `low` | `tight_range` | `in_range`
  | `high` | `very_high`; `AlertWeekday`: `sunday` … `saturday`; `TrendBucket`;
  `AlertComparisonOperator`: `>` `>=` `<` `<=` `==`; the composite operator, `direction`
  and `alert_state` `state` words)
- `StateSpanCategory` and `PumpModeState` are domain enums the rest of the API also
  writes, as their **PascalCase member names** (`"Override"`, `"PumpConnectivity"`,
  `"Automatic"`, `"EaseOff"`, …); a condition uses the same names
- a `time_of_day` `timezone` is exactly an IANA tzdb name (§4)

### 1.2 ConditionNode

A node is `{ "type": "<discriminator>", "<payload-field>": { … } }`. The payload field
name equals the type string for every kind (e.g. `"threshold": {…}`,
`"rate_of_change": {…}`, `"alert_state": {…}`). A node holds exactly its `type`, that one
payload, and optionally the rule editor's `_uid` key.

- `type` names a kind only when it is **exactly** that kind's wire string. Any other
  string (`"Threshold"`, `"RateOfChange"`, `"3"`, `"warp_drive"`) names no kind.
- A node with no payload property for its `type`, or a `null` one, has no payload.
- The full discriminator set is the
  `AlertConditionType` enum's `EnumMember` values.

### 1.3 Numerics

Glucose, rate, IOB and the other measured values compare as exact decimals (`rust_decimal`,
the C# `decimal` semantics: 28-29 significant digits). Comparisons like `5.5 == 5.5` and
boundary cases (`>=` at exactly the threshold) are exact-decimal; binary floats are not
acceptable.

Elapsed-time leaves are mixed:

- `staleness`, `loop_stale`, `loop_enaction_stale`, `site_age`, `sensor_age`,
  `tracker_age` compute elapsed as `(decimal)(now - anchor).TotalMinutes/Hours/Days` —
  i.e. **`double` arithmetic first, then cast to decimal**, then exact-decimal
  comparison.
- `alert_state` (`for_minutes`), `pump_suspended`, `override_active`, `do_not_disturb`,
  `pump_state`, `state_span_active`, `sustained`, `time_since_last_carb/bolus` compare in
  **`double`** (`.TotalMinutes >= forMinutes`).

The corpus pins boundary behaviour: the *comparison operator and inclusivity*, elapsed-time
math in f64, and the conversion to decimal only where the list above says.

### 1.4 Evaluability

A tree is evaluated only as written. The engine reads a whole tree before evaluating
anything, and rejects it when any node, anywhere, has one of these problems, whether or not
a given tick would reach that node (under an `or` already true, a leaf whose input is null
this tick):

| Reason code | Shape |
|---|---|
| `not_an_object` | a node or payload that is not a JSON object |
| `too_deep` | nesting past the reader's limit (§1.1) |
| `type_missing` | a node with no `type` (a child, or an auto-resolve / snooze root) |
| `unknown_kind` | a `type` that is not exactly a kind's wire string (§1.2) |
| `payload_missing` | a node with no payload for its `type`, or a rule body whose `condition_params` is JSON `null` |
| `condition_missing` | a `null` slot in a composite's `conditions` |
| `unknown_field` | a node property other than `type`, `_uid` and its own kind's payload, or a payload property its kind does not read (names match exactly, §1.1), so a misspelt operand such as `timeoutMinutes` or `Value` is not silently dropped. No field is named: the property is the author's text |
| `field_missing` | a required operand is absent or `null`: every operand except `for_minutes`, `timezone` and `state_span_active` `state`, and the `child` of a `not` or `sustained` |
| `invalid_field` | a value of the wrong JSON kind, a number out of range, an enum operand that is not a string, or a `timezone` that is not exactly an IANA zone id (§4) |
| `unknown_value` | an enum operand naming no member (§1.1): the composite `operator`, a comparison `operator`, `direction`, `alert_state` `state`, `trend` `bucket`, `temp_basal` or `transmitter_battery` `metric`, `pump_state` `mode`, `state_span_active` `category`, or a `glucose_bucket` or `day_of_week` list member |
| `list_empty` | a composite's `conditions`, a `glucose_bucket` `buckets` or a `day_of_week` `days` that is absent, `null` or empty |
| `minutes_not_positive` | a duration `<= 0` that leaves its node never true: `sustained` `minutes`, `signal_loss` `timeout_minutes`, `predicted` `within_minutes` (no prediction is at or before now) |
| `minutes_negative` | a negative bound on an elapsed time: `staleness` `value`, `minutes` on `loop_stale`/`loop_enaction_stale`/`time_since_*`. Elapsed time is never negative and an absent anchor reads as infinite, so every operator would compare the same way on every tick. `tracker_age` is exempt: its elapsed time is negative before a scheduled event |
| `invalid_time` | a `time_of_day` `from` or `to` that is not exactly `HH:mm` (`"9:00"`, `"24:00"`) |
| `empty_window` | a `time_of_day` whose `from` equals its `to`, a window that never opens |
| `pump_mode_category` | a `state_span_active` on the `PumpMode` category; pump modes are `pump_state`'s |

A tree that cannot be evaluated:

- Rule body: the rule is skipped — no root, leaves, tracker transition, auto-resolve or
  timer writes. The driver returns `skipped` and the `evaluate` envelope returns an error,
  which the host's per-rule catch treats as the same skip, so a live alert keeps its state
  rather than resolving. The host tells the rule's recipients once per version of the body,
  through the rule's channels at its severity and behind the Do Not Disturb gate its excursions
  pass, that it cannot fire until it is edited (`UnevaluableRuleNotice`). Only the alert sweep
  sends it, every run, so it does not wait for a reading and evaluation never waits on it; a
  run that stops between recording the notice and dispatching it is made good by the next. Once
  the delivery rows are written it behaves as an excursion's dispatch does: a channel whose
  provider fails is recorded failed and is not retried. The notice is a payload of kind
  `unevaluable`, with no glucose value, and is never raised on the Nightscout `/alarm`
  namespace or broadcast to the alerts hub. It is resolved when the body can be evaluated
  again, when it changes to another that cannot, and when the rule is disabled or deleted. Each
  sweep run also resolves any notice whose rule is no longer enabled or no longer has that
  broken version, whatever path missed it, so a rule enabled again while still broken is told
  again. A standing notice's excursion ends at `infinity` (so the 24-hour history clamp never
  hides it) and every query over `alert_excursions` and `alert_instances` excludes notices
  through `AlertExcursionQueries` unless it states why it need not.
- Auto-resolve tree: never resolves (§7 step 5, as for malformed JSON).
- Snooze conditions: the snooze clears.

The crate's `validate` entry point reports every problem in a rule's evaluated trees, in
pre-order; `AlertRulesController` returns them as a 400, and a rule's stored problems are
shown in the rule editor. Each issue carries its scope (`condition`, `auto_resolve`,
`snooze`), the node's condition path (§2.3; a null slot's path ends in `.`), the reason
code and, where there is one, the field. Reason codes never carry payload values.

---

## 2. Tree structure, leaf identity, and paths

### 2.1 Containers

Exactly three kinds are containers: `composite` (N children), `not` (1 child),
`sustained` (1 child). Everything else is a leaf.

A container always has its children: one without them cannot be evaluated (§1.4).

### 2.2 Leaf IDs

Pre-order depth-first, left-to-right. Containers are unwrapped (never receive IDs);
every non-container node gets the next sequential ID starting at 0. ID space is
per-rule-tree per call.

### 2.3 Condition paths

Paths are the **persistent identity for sustained timers** (`alert_condition_timers`
keyed by `(rule_id, path)`). The Rust crate owns path generation and must match
byte-for-byte:

- Root segment: the root node's kind, **no index**.
  - In the live orchestrator the root path is the **canonical wire string** of the
    rule's `ConditionType` (`AlertConditionTypeNames.ToWireString`).
  - Auxiliary evaluation scopes use reserved roots instead: **`auto_resolve`** (both the
    per-reading and sweep auto-resolve paths) and **`snooze`** (smart-snooze condition
    evaluation). Same `(rule_id, path)` timer rows are intentionally shared between the
    per-reading and sweep variants of the same scope.
- Each descendant appends `[{childIndex}].{child.Type}`, the child's wire string (§1.2).
  An issue on a node that cannot be read (§1.4) names its `type` as written, or an empty
  segment when it has none. `not` and `sustained` children are always index `[0]`.
- Example: `composite[0].sustained[0].threshold`, `composite[1].not[0].iob`.

### 2.4 Evaluation order

Every node evaluates to **true**, **false** or **unknown**. A node is unknown when a fact it
compares is one the host has not got (§3), as opposed to a state whose absence is itself the
answer. The connectives are Kleene's strong three-valued logic:

| `a` | `b` | `a and b` | `a or b` | `not a` |
|---|---|---|---|---|
| true | true | true | true | false |
| true | false | false | true | false |
| true | unknown | unknown | true | false |
| false | false | false | false | true |
| false | unknown | false | unknown | true |
| unknown | unknown | unknown | unknown | unknown |

So `and` is false when any child is false, else unknown when any is unknown, else true; `or`
is true when any child is true, else unknown when any is unknown, else false; `not` keeps
unknown. `not(threshold above 180)` with no reading is unknown, not true.

`composite` evaluates **every child, in document order, with no short-circuit**, then
combines the results. A child is evaluated whatever an earlier sibling returned, so every
`sustained` descendant sets, keeps or clears its timer on each evaluation of its parent: where
a row sits among its siblings never changes when a rule becomes true. **[normative]**

On the wire (`evaluate`, `evaluate_node`, the replay output) a root, a leaf and a replay
`met` stay booleans, `false` when unknown, with `"unknown": true` beside them only then. A
reader that ignores the flag reads unknown as false; in a leaf log it can then see a point
whose `value` repeats the one before (unknown, then false), which changes nothing.

The replay path additionally force-evaluates **every leaf in isolation** to build the per-leaf
transition log; leaves are evaluated with the **rule-root context path**, not their tree path (irrelevant for stateless leaves;
containers are never force-evaluated). A leaf evaluation that throws is recorded `false`. A
leaf that turns unknown, or known again, is a transition in the log.

---

## 3. Leaf semantics

Shared comparison helper (`ComparisonOps.Compare(actual, op, threshold)`): operators are
`<` `<=` `>` `>=` `==` (§1.4). All comparisons below are non-strict/strict exactly as
written. Every operand below is present and valid (§1.4). The last column says when a leaf
is unknown (§2.4); a leaf is known in every other case, including the absent states it lists
as false or true.

| Kind | Payload | Inputs | Semantics | Unknown, and absence that is an answer |
|---|---|---|---|---|
| `threshold` | `direction` (`above`\|`below`), `value` | `LatestValue` | below: `v < value`; above: `v > value` (**strict**) | `LatestValue` null, or the reading stale ⇒ **unknown** |
| `rate_of_change` | `direction` (`rising`\|`falling`), `rate` | `TrendRate` (mg/dL/min, stored or derived, §4) | falling: `rate' <= -rate`; rising: `rate' >= rate` (**inclusive**) | `TrendRate` null, or the reading stale ⇒ **unknown** |
| `staleness` | `operator`, `value` (minutes, int) | `LastReadingAt`, `LatestTimestamp`, the rule's `enabled_at`, now | `Compare((decimal)(now-last).TotalMinutes, op, value)` | no reading history ⇒ measured from `enabled_at`, and **unknown** without it (below); only `LastReadingAt` null ⇒ elapsed = +∞: `>`/`>=` ⇒ true, others ⇒ false |
| `predicted` | `operator`, `value`, `within_minutes` | `Predictions[]` | true if **any** point with `OffsetMinutes <= within_minutes` satisfies `Compare(point.Mgdl, op, value)`; points beyond horizon are skipped, not range-checked below | empty predictions ⇒ **unknown**; with points present but none within the horizon or satisfying ⇒ false |
| `trend` | `bucket` (wire string) | `TrendBucket` (of the rate, else of the arrow, §4) | equality of wire forms (`rising_fast` … `falling_fast`, `unknown`) | `TrendBucket` null, a name the engine has no member for (§4), or the reading stale ⇒ **unknown** |
| `time_of_day` | `from`, `to` (`HH:mm` exact, invariant), `timezone` (IANA, nullable) | now | half-open `[from, to)` in resolved tz; `from > to` wraps midnight (`current >= from \|\| current < to`) | Tz resolution: the per-rule tz when set; else tenant tz (`TenantTimeZoneId`), unknown tenant tz ⇒ **UTC**; both absent ⇒ UTC; never unknown |
| `iob` | `operator`, `value` (units) | `IobUnits` | `Compare` | null ⇒ **unknown** |
| `cob` | `operator`, `value` (grams) | `CobGrams` | `Compare` | null ⇒ **unknown** |
| `reservoir` | `operator`, `value` (units) | `ReservoirUnits`, `ReservoirIsLowerBound` | `Compare`; a lower-bound reading `b` ("50+") means the reservoir holds at least `b`, so it decides a comparison only where every amount from `b` up gives the same answer (table below) | null ⇒ **unknown**; a lower-bound reading that does not decide the comparison ⇒ **unknown** |
| `site_age` | `operator`, `value` (**hours**) | `LastSiteChangeAt`, now | `Compare((decimal)(now-anchor).TotalHours, op, value)` | null ⇒ **unknown** |
| `sensor_age` | `operator`, `value` (**days**) | `LastSensorStartAt`, now | `Compare((decimal)(now-anchor).TotalDays, op, value)` | null ⇒ **unknown** |
| `alert_state` | `alert_id` (Guid), `state`, `for_minutes?` | `ActiveAlerts[alert_id]`, now | `firing`: snapshot.State == "firing" (ci). `unacknowledged`: State == "firing" **and** AcknowledgedAt null. `acknowledged`: AcknowledgedAt non-null (State irrelevant). Then if `for_minutes` set: elapsed since anchor `>= for_minutes` (double); anchor = AcknowledgedAt for `acknowledged`, TriggeredAt otherwise | no snapshot for id ⇒ false (the other alert is not firing) |
| `loop_stale` | `operator`, `minutes` | `LastApsCycleAt`, `HasEverApsCycled`, now | `Compare((decimal)(now-cycle).TotalMinutes, op, minutes)` | `HasEverApsCycled` false ⇒ **unknown**; `LastApsCycleAt` null ⇒ **unknown** (no infinity convention here) |
| `loop_enaction_stale` | `operator`, `minutes` | `LastApsEnactedAt`, `HasEverApsCycled` | same as `loop_stale` against the enacted timestamp | same guards (note: guard is `HasEverApsCycled`, not a separate enaction flag) |
| `pump_suspended` | `is_active`, `for_minutes?` | `ActivePumpSuspension`, `HasEverPumpSnapshot`, now | `(suspension != null) == is_active`; then if `for_minutes` set **and** `is_active` true: `(now - StartedAt).TotalMinutes >= for_minutes` (double); `for_minutes` with `is_active:false` is a no-op (true) | `HasEverPumpSnapshot` false ⇒ **unknown**; with it true, as the semantics say |
| `pump_battery` | `operator`, `value` (%) | `PumpBatteryPercent`, `HasEverPumpSnapshot` | `Compare` | guard false ⇒ **unknown**; percent null ⇒ **unknown** |
| `temp_basal` | `metric` (`rate`\|`percent_of_scheduled`), `operator`, `value` | `ActiveTempBasal` | `rate`: compare `Rate` (U/hr). `percent_of_scheduled`: compare `Rate / ScheduledRate * 100` (decimal division) | no active temp ⇒ false (**no** HasEver guard, by design: no temp is the state, for `rate` too); `percent_of_scheduled` with `ScheduledRate` null or 0, or a percentage that overflows ⇒ **unknown** |
| `uploader_battery` | `operator`, `value` (%) | `UploaderBatteryPercent`, `HasEverUploaderSnapshot` | `Compare` | guard false ⇒ **unknown**; percent null ⇒ **unknown** |
| `transmitter_battery` | `metric` (`voltage_a`\|`voltage_b`\|`resistance`\|`days`), `operator`, `value` | `TransmitterVoltageA`, `TransmitterVoltageB`, `TransmitterResistance`, `TransmitterDays` | `Compare` against the metric's reading, unscaled as the transmitter reports it | reading null ⇒ **unknown** (**no** HasEver guard) |
| `override_active` | `is_active`, `for_minutes?` | `ActiveOverride`, now | same shape as `pump_suspended` | **no** HasEver guard (absence of override is legitimate): never unknown |
| `sensitivity_ratio` | `operator`, `value` | `SensitivityRatio`, `HasEverApsSensitivity` | `Compare` | guard false ⇒ **unknown**; ratio null ⇒ **unknown** |
| `do_not_disturb` | `is_active`, `for_minutes?` | `ActiveDoNotDisturb`, now | same shape as `pump_suspended` (anchor `StartedAt`); `for_minutes` no-op on the inactive side | no guard; snapshot null = DND off: never unknown |
| `glucose_bucket` | `buckets: [..]` | `GlucoseBucket` | set membership | bucket null, a name the engine has no member for (§4), or the reading stale ⇒ **unknown** |
| `time_since_last_carb` | `operator` (enum), `minutes` | `LastCarbAt`, now | elapsed minutes (double) compared via `AlertComparisonOperator`; **null anchor ⇒ elapsed = +∞** (so `>`/`>=` fire on cold start; intentional, opposite of loop_stale) | `==` against +∞ ⇒ false; never unknown |
| `time_since_last_bolus` | same | `LastBolusAt` | same | same |
| `day_of_week` | `days: [..]` (`AlertWeekday` names) | `TenantTimeZoneId`, now | local `now.DayOfWeek ∈ days` | tenant tz null/unknown ⇒ UTC; never unknown |
| `pump_state` | `mode`, `is_active`, `for_minutes?` | `ActivePumpState` | `is_active:false`: true whenever active mode ≠ `mode` (**including no active span**). `is_active:true`: mode must match; `for_minutes` anchors at `StartedAt` (double, `>=`) | no guard; no active span is an answer; an active span whose mode is a name the engine has no member for (§4) is unknown wherever the answer depends on that mode (table below) |
| `state_span_active` | `category`, `state?`, `is_active`, `for_minutes?` | `ActiveStateSpans[(category, state)]` | `is_active:false`: true iff no snapshot under the exact `(category, state)` key (null state = "any of category": the enricher loads that key shape). `is_active:true`: snapshot must exist; `for_minutes` anchors at `StartedAt` | no guard: never unknown; a span of a category the engine has no member for (§4) is not in the lookup |
| `tracker_age` | `tracker_definition_id` (Guid), `operator`, `minutes` | `ActiveTrackers[tracker_definition_id]`, now | `Compare((decimal)(now-reference).TotalMinutes, op, minutes)`; reference is the active instance's start (Duration) or scheduled time (Event), resolved by the enricher; elapsed is **negative before a scheduled event**, so negative `minutes` with `>=` means "within N minutes before the event". For a **Duration** instance, a threshold that already held when the instance was recorded is **false** for that instance: with `>=` when `reference + minutes <= RecordedAt`, with `>` when `reference + minutes < RecordedAt`. So a run entered after the fact does not alert for what it has passed, and a later threshold fires as usual | no active instance for the definition ⇒ **false**, never unknown: a tracker that isn't running has no age (deliberately opposite to `time_since_last_*` cold-start infinity). `RecordedAt` null, an **Event** instance (`Scheduled`), or `<`/`<=`/`==` ⇒ nothing is held back |

`sleep_session_active` (`is_active`; `SleepSessionActive`; true while the flag equals
`is_active`) is never unknown: no session is the state.

`pump_state` by what the pump is in, for a leaf naming mode `X`. An unrecognised mode is one
the engine has no member for (§4): the pump may or may not be in `X`, so the leaf is unknown
wherever the answer differs between the two, and `for_minutes` on the inactive side is a
no-op as for every span leaf. On the active side with `for_minutes`, a span that has not yet
lasted that long is false whatever its mode, so the leaf is false until then and unknown from
then on (§5.3 reports that instant).

| Pump is | `is_active` | no `for_minutes` | `for_minutes` not yet held | `for_minutes` held |
|---|---|---|---|---|
| no active state | true | false | false | false |
| no active state | false | true | true | true |
| in `X` | true | true | false | true |
| in `X` | false | false | false | false |
| in another known mode | true | false | false | false |
| in another known mode | false | true | true | true |
| in an unrecognised mode | true | unknown | false | unknown |
| in an unrecognised mode | false | unknown | unknown | unknown |

**Glucose freshness.** `threshold`, `rate_of_change`, `trend` and `glucose_bucket` read their
glucose fact only while the reading it came from is fresh. A reading's age runs from its
**arrival**, `LatestReceivedAt` (§4), or from `LatestTimestamp` when the context has no
arrival. The reading is **stale** at `now` when `now - arrival > bound`, compared as exact
durations: a reading exactly `bound` old is fresh, and one a tick older is stale. So a reading
that arrives late, from a connector that delivers readings 20 minutes after they were taken, is
fresh when it arrives and goes stale one bound later, and a real outage reads unknown one bound
after the last arrival. A stale reading's facts are unknown to these four leaves, as an absent
one is. `bound` is the rule's `glucose_freshness_minutes`, a whole number of minutes from 1 to
1440, or **11 minutes** when the rule has none; any other value is an error in the `evaluate`
and `replay` envelopes. An evaluation with no rule (`evaluate_node`, the snooze) uses 11
minutes. With neither time the reading has no age and is not judged: the leaves read the facts
as they are, which a host sends only with a reading time. An arrival after `now` is fresh.

The default suits a source that sends a reading every 5 minutes: a reading is fresh until the
next is due, with room for one late arrival. On a slower source the default runs out between two
readings that arrive on time, so every reading interval has an instant where the glucose
leaves read unknown. Under `hold` that changes nothing (§5.1), but a rule whose `on_unknown` is
`met` or `not_met` reads it as a known value: `not_met` moves an open excursion into hysteresis,
and closes it, once per reading interval, and `met` opens one on data with no fault. Such a rule
on a source slower than 5 minutes needs its own `glucose_freshness_minutes`, at least the
interval between readings plus the delay a reading can arrive with.

**No reading ever.** `staleness` and `signal_loss` measure the time since data stopped. With
no reading history at all (both `LastReadingAt` and `LatestTimestamp` null), data stopped when
the rule was last enabled: the rule's `enabled_at`, an instant the host sends beside
`on_unknown`. The leaf then measures `now - enabled_at` as it measures a reading's age:
`signal_loss` is true once `now - enabled_at >= timeout_minutes`, and `staleness` compares
`now - enabled_at` with its operator and value. So a sensor that never sends a reading is noticed
one bound after the rule is enabled. The leaf is known from `enabled_at` on, and a comparison that
holds while the elapsed time is short holds from the start: `staleness` `<` and `<=` are true from
`enabled_at` until the bound, as is `not(signal_loss)`. An `enabled_at` after `now` is a negative
elapsed time, as a reading timestamped after `now` is: `signal_loss` and `staleness` `>`, `>=`
and `==` are false, `<` and `<=` true, and the bound is still reached at `enabled_at` plus the
bound. With no `enabled_at` either (a host that does not send it, a rule stored before the host
recorded it) the leaf is **unknown**. Any reading history decides as it always has, and
`enabled_at` is not read. An evaluation with no rule has no `enabled_at`, so these leaves read
unknown there without a reading: `evaluate_node`, which the backend uses for the snooze and for
the sweep's periodic auto-resolve (§6.1, §7 step 5), unlike the auto-resolve inside `evaluate`,
which reads the rule's `enabled_at`. The leaves stay ordinary leaves: under `not`, `sustained`
and composites they follow from this with no special case.

`staleness` and `signal_loss` are not judged: they say that data stopped, so they read an old
reading as they always have. So `or(signal_loss, threshold below 70)` stays true through an
outage once the signal is lost (Kleene `or`, §2.4), and opens.

`predicted` is not judged either. Its points are not facts of the reading: the host fetches
them when it evaluates and gives each an offset from the forecast's own start, and the context
carries no anchor for them, so the reading's age says nothing about theirs. How old a forecast
the host sends is the host's choice: the backend's device-status source sends none older than
its recency limit, and its own forecast runs from the newest readings it has.

Under `hold` (§6.4) a rule with a stale fact opens nothing, an excursion open when the reading
went stale stays open and does not enter hysteresis, a `sustained` timer over a stale leaf is
neither set nor cleared, and the first fresh reading decides. Under `not_met` a stale fact is a
known false: an open excursion goes through hysteresis during the outage and closes as on a false
reading. Under `met` it is a known true.

A **lower-bound** `reservoir` reading `b` (`ReservoirIsLowerBound`, "50+") is read exactly:
the reservoir holds at least `b`.

| Operator | true when | false when | unknown otherwise |
|---|---|---|---|
| `>` | `b > value` | never | `b <= value` |
| `>=` | `b >= value` | never | `b < value` |
| `<` | never | `b >= value` | `b < value` |
| `<=` | never | `b > value` | `b <= value` |
| `==` | never | `b > value` | `b <= value` |

`composite` / `not` / `sustained` (containers):

- **`composite`** — `{operator, conditions[]}`, `and` or `or` over at least one child.
  Evaluates every child (§2.4) and combines them in Kleene's logic.
- **`not`** — `{child}`. Inverts the child; unknown stays unknown.
- **`sustained`** — `{minutes, child}`, `minutes > 0`. Each evaluation: evaluate child
  first (path-extended), then:
  - child false ⇒ **clear** the `(rule_id, path)` timer, return false;
  - child unknown ⇒ return **unknown** and leave the timer as it is, set or not: a run is
    not broken by not knowing, and not started by it;
  - child true, no timer ⇒ **set** timer to `now`, return **false** (a 0-elapsed check
    does not happen on the set tick);
  - child true, timer exists ⇒ return `(now - first).TotalMinutes >= minutes` (double).
  - The timer key path is the path **of the sustained node itself**, not the child.

---

## 4. SensorContext

The context is assembled host-side (enricher in the backend; local assembler in
Prelude). For the crate it is pure input data. Fields and their consumers are the table
in §3 plus:

- `LatestReceivedAt` is when the host received the newest reading, the instant its glucose
  facts' age runs from (§3). The backend sends the instant it stored the reading's row: a
  re-upload under the reading's sync key, or an edit, updates that row and keeps the instant; a
  copy that deduplication links into the reading's group is a duplicate, which canonical reads
  do not return; and an older reading that arrives later is not the newest (the newest is the
  latest by `LatestTimestamp`). A copy that becomes the newest some other way (a new primary after
  the reading is deleted, a reading deduplication does not match) carries its own instant.
  Absent, the age runs from `LatestTimestamp`.
- `TrendRate` is the reading's stored (vendor) rate, used unchanged. Without one, it is
  derived as `(mgdl - previous.mgdl) / minutes between them` from the nearest earlier
  reading of the same stream and the same uploader with a glucose value. A stream is the
  registered device the reading is attributed to, else its `(data_source, device)` pair. The
  uploader is the reading's `data_source`; a null and an empty one are the same uploader and
  match nothing else. So when two apps upload one registered CGM, another app's copy of a
  reading is never the earlier reading, linked by deduplication or not. That earlier reading
  may be a copy deduplication hides or a reading of a bucket its stream lost to another
  stream, but never a copy in the reading's own duplicate group (one uploader can send a
  reading twice under two ids at one timestamp). A pair under **30 seconds** apart
  (a duplicate timestamp included) or over **15 minutes** apart gives null; exactly 30
  seconds and exactly 15 minutes derive. A reading without a glucose value derives nothing.
  Replay derives by the same rule from the readings it loads.
- `TrendBucket` is the bucket of `TrendRate` (`>= 3` rising_fast, `>= 1` rising, `>= -1`
  flat, `>= -3` falling, else falling_fast, mg/dL/min). With `TrendRate` null it is the
  bucket of the reading's stored arrow (`DoubleUp` rising_fast, `SingleUp`/`FortyFiveUp`
  rising, `Flat` flat, `FortyFiveDown`/`SingleDown` falling, `DoubleDown` falling_fast;
  any other arrow, or none, gives null). The arrow never becomes a `TrendRate`.
- **An enum name the engine has no member for.** A host newer than the engine can send one in
  the context (a bucket, a pump mode, a state-span category). The context still reads, and the
  name degrades only the fact that carries it, by what each leaf can honestly say:
  - `trend_bucket` and `glucose_bucket`: read as an absent bucket, so `trend` and
    `glucose_bucket` are **unknown**, whatever bucket the leaf names.
  - `active_pump_state.mode`: the pump is in some mode the engine cannot name, so it may or may
    not be the mode a `pump_state` leaf names. The leaf is **unknown** for every configured
    mode wherever the answer depends on it, and **false** where every mode gives false (the
    table in §3); the one change over time, false to unknown when `for_minutes` has passed,
    is a next change (§5.3).
  - `active_state_spans[].category`: the span is ignored. A `state_span_active` leaf names a
    category the engine knows, and a span of a name the engine does not know is a different
    category, so it can neither be the leaf's span nor stand for its absence. The leaf reads
    the known spans alone. This assumes a category keeps its name: a host that renames one the
    engine knows must send the new name only once the engine knows it.
  - A state span's `state` is free text matched exactly, not an enum.

  A condition operand naming an unknown member is a different case: the tree cannot be
  evaluated (§1.4).
- `CurrentRuleId` / `CurrentPath`: threading state for sustained timers; provided by the
  driver (orchestrator/sweep/replay), maintained by the container evaluators as they
  descend.
- Cold-start flags `HasEverApsCycled`, `HasEverPumpSnapshot`, `HasEverUploaderSnapshot`,
  `HasEverApsSensitivity`: distinguish "never had this data" from "data is old/absent".
  Guarded leaves are listed in §3; a guard that is false makes its leaf unknown. Facts whose
  absence is naturally meaningful
  (`ActiveTempBasal`, `ActiveOverride`, `ActiveDoNotDisturb`, `ActivePumpState`,
  state spans) have **no** flag.
- `ActiveAlerts`: `Dictionary<Guid, {State, TriggeredAt, AcknowledgedAt}>` — live state
  of *other* rules, for `alert_state`. State strings observed: `"firing"` (open
  excursion); `AcknowledgedAt` non-null marks acknowledged.
- Timezone resolution order (where applicable): per-rule condition timezone → tenant
  timezone (`TenantTimeZoneId`, IANA id) → UTC.
- **Which ids resolve** is itself normative, because it decides whether a rule fires at
  all. A per-rule `time_of_day.timezone` is condition JSON (§1.1): it is exactly an IANA
  tzdb name, including tzdb *backward links* (`Etc/Greenwich`, `US/Pacific`,
  `Asia/Calcutta`). Anything else, a Windows id (`AUS Eastern Standard Time`), another
  case (`ETC/GMT-2`) or `""`, is `invalid_field` on `timezone`, so the tree cannot be
  evaluated (§1.4).
- The tenant zone is tenant data, not condition JSON. The crate resolves an exact IANA id,
  then retries case-insensitively over `chrono_tz::TZ_VARIANTS`, since connector data
  carries mis-cased ids in bulk (production has ~240 rows spelling `Etc/GMT-2` as
  `ETC/GMT-2`). A Windows id does not resolve (`chrono_tz` has no CLDR mapping), so the
  .NET host rewrites one to IANA before it sends the tenant zone; another host must do the
  same. An unresolved tenant zone falls back to UTC.

---

## 5. `signal_loss` and wall-clock evaluation

`signal_loss` is an ordinary leaf, at the root or inside a tree:

| Payload | Inputs | Semantics | Null / guard behaviour |
|---|---|---|---|
| `timeout_minutes` (int) | `LastReadingAt`, `LatestTimestamp`, the rule's `enabled_at`, now | `now - LastReadingAt >= timeout_minutes`, compared as exact durations (no minute rounding) | no reading history ⇒ `now - enabled_at >= timeout_minutes`, and **unknown** without `enabled_at` (§3, "No reading ever"); only `LastReadingAt` null ⇒ infinitely stale ⇒ true |

It is `staleness{operator: ">=", value: timeout_minutes}` with an exact-duration compare.

The leaf can only turn true *between* readings — a reading arriving sets `LastReadingAt`
to the reading's time — so per-reading evaluation alone never fires it. It is one of the
wall-clock kinds below.

### 5.1 Wall-clock evaluation

A **wall-clock kind** is one whose truth can change while every fact except `now` stands
still: it measures elapsed time against an anchor that a reading does not move.

| Wall-clock | Anchor |
|---|---|
| `signal_loss`, `staleness` | newest reading; with no reading history, the rule's `enabled_at` (§3) |
| `loop_stale`, `loop_enaction_stale` | last loop cycle / enactment |
| `site_age`, `sensor_age`, `tracker_age` | site change, sensor start, tracker start or schedule |
| `time_since_last_carb`, `time_since_last_bolus` | last treatment |
| `alert_state` | the other alert's trigger or acknowledgement (`for_minutes`) |
| `pump_suspended`, `override_active`, `do_not_disturb`, `pump_state`, `state_span_active` | start of the span (`for_minutes`) |

Every other kind is not wall-clock. Two cases are deliberate:

- **`sustained`** is a container. Over a reading-driven child its timer only extrapolates
  the last reading, so it does not by itself make a rule wall-clock.
- **`time_of_day`, `day_of_week`** are calendar gates over reading-driven leaves; the next
  reading bounds how late a gate opening is noticed.

A rule is wall-clock when its root kind, any node of its condition tree, or, while
auto-resolve is enabled, any node of its auto-resolve tree is a wall-clock kind. Every
host must evaluate enabled wall-clock rules periodically as well as per reading, through
the normal driver sequence (§7). Otherwise such a rule stays unfired for as long as no
reading arrives. A host may instead evaluate such a rule at its last evaluation's
`next_change_at` (§5.3), but only while every fact the rule reads (§5.2) is one that
moves only through writes that evaluate the rule. The backend does this from
`AlertSweepJob` every 30 s per tenant; Prelude needs an equivalent evaluation. The
crate's `wall_clock::references_wall_clock` reads a rule's kinds as §5.2 does, and the
backend's sweep asks it which rules to evaluate.

A periodic evaluation uses the context the per-reading path builds for the newest
reading, with only `now` advanced. Its `LastReadingAt` / `LatestTimestamp` /
`LatestReceivedAt` are that reading's (null when there has never been one), and its glucose
facts are that reading's however old. Up to the rule's freshness bound after the reading's
arrival (§3) the reading-driven leaves repeat the verdict of the last per-reading evaluation;
past it they read unknown, as they would on any evaluation then.

A rule whose `on_unknown` is `met` or `not_met` (§6.4) is wall-clock as well when its root kind,
a node of its condition tree or, while auto-resolve is enabled, a node of its auto-resolve tree
is `threshold`, `rate_of_change`, `trend` or `glucose_bucket`: such a leaf turns unknown when
its reading goes stale, with only `now` advancing, and the rule reads that as a known value that
moves the tracker. `references_wall_clock` reads the rule's `on_unknown` for this.

Under `hold` a rule needs no evaluation at that instant, unless another of its nodes changes
there (§5.3). Going from known to unknown, a leaf
cannot turn a known root into the other known value (Kleene's connectives are monotone in what
is known, and `sustained` returns unknown on an unknown child), so an evaluation there either
reads the root an evaluation a tick earlier reads, or holds. A held evaluation sets and clears no
timer over the stale leaf, makes no tracker call (§6.4), resolves nothing and keeps a re-arm hold
(§6.3); the one thing it does, closing an elapsed hysteresis window, the sweep does for every rule
whatever it evaluates (§6.1). So the evaluation is the same as no evaluation, and the next one,
per reading or periodic, decides.

The excursion tracker dedupes: a continuing outage is `continues`, and the first evaluation
after readings resume feeds false, so the excursion goes through hysteresis and closes like
any other.

A periodic evaluation is an evaluation to the tracker, so it advances confirmation (§6).
The backend stores no `ConfirmationReadings` or `HysteresisMinutes`: every rule reads 1
and 0, and the alerts redesign migrated both into `sustained` wrappers, which are
cadence-independent. So no stored rule is affected. A host that does carry `confirmation_readings > 1` for a wall-clock rule would
confirm it N× faster at a shorter periodic cadence. It should express that confirmation as
`sustained` instead.

### 5.2 Context facts

A **context fact** is a group of `SensorContext` fields (§4) that a host fills from one
source. The crate's `facts::reads(kind)` gives the facts a kind's own evaluator reads, and
`facts::rule_reads(rule)` the facts of every kind of a stored rule; `references_any(rule,
facts)` is whether that set meets `facts`.

A rule's kinds are its root kind, the kinds of its body and, while auto-resolve is enabled,
the kinds of its auto-resolve tree. The root `condition_type` is exactly a kind's wire
name, and anything else gives the rule no kinds. The root kind counts
whatever its body. The body's kinds count only when it can be evaluated (§1.4), nested
kinds resolving as node dispatch resolves them (§1.2). The auto-resolve tree's kinds count
whenever auto-resolve is enabled and the tree is present, not `null` and evaluable, even
when the body is not and evaluation would skip the rule. `references_wall_clock` (§5.1)
reads a rule's kinds the same way.

| Fact | `SensorContext` fields | Kinds that read it |
|---|---|---|
| `glucose` | `LatestValue`, `LatestTimestamp`, `LatestReceivedAt`, `LastReadingAt`, `TrendRate`, `TrendBucket`, `GlucoseBucket` | `threshold`, `rate_of_change`, `signal_loss`, `staleness`, `trend`, `glucose_bucket` |
| `predictions` | `Predictions` | `predicted` |
| `insulin` | `IobUnits`, `LastBolusAt`, `ActiveTempBasal` | `iob`, `temp_basal`, `time_since_last_bolus` |
| `carbs` | `CobGrams`, `LastCarbAt` | `cob`, `time_since_last_carb` |
| `pump` | `ReservoirUnits`, `ReservoirIsLowerBound`, `PumpBatteryPercent`, `HasEverPumpSnapshot` | `reservoir`, `pump_battery`, `pump_suspended` |
| `aps` | `LastApsCycleAt`, `LastApsEnactedAt`, `HasEverApsCycled`, `SensitivityRatio`, `HasEverApsSensitivity` | `loop_stale`, `loop_enaction_stale`, `sensitivity_ratio` |
| `uploader` | `UploaderBatteryPercent`, `HasEverUploaderSnapshot` | `uploader_battery` |
| `transmitter` | `TransmitterVoltageA`, `TransmitterVoltageB`, `TransmitterResistance`, `TransmitterDays` | `transmitter_battery` |
| `state_spans` | `ActiveOverride`, `ActivePumpSuspension`, `ActivePumpState`, `ActiveStateSpans` | `override_active`, `pump_suspended`, `pump_state`, `state_span_active` |
| `device_ages` | `LastSiteChangeAt`, `LastSensorStartAt` | `site_age`, `sensor_age` |
| `trackers` | `ActiveTrackers` | `tracker_age` |
| `sleep` | `SleepSessionActive` | `sleep_session_active` |
| `active_alerts` | `ActiveAlerts` | `alert_state` |
| `do_not_disturb` | `ActiveDoNotDisturb` | `do_not_disturb` |
| `time_zone` | `TenantTimeZoneId` | `time_of_day`, `day_of_week`, `glucose_bucket` |

The containers `composite`, `not` and `sustained` read no fact; a `sustained` timer is
evaluation state, not context. `pump_suspended` reads two facts: its guard is the pump
snapshot and its anchor the suspension span. No field carries a therapy profile or
schedule as such; the target range reaches the context only through `GlucoseBucket`, which a
host derives from the reading and the schedule at the tenant's local time, so
`glucose_bucket` reads `time_zone` as well as `glucose`.
`now` is not a fact: it is an argument to evaluation.

A host uses facts to evaluate rules on writes other than glucose: a write evaluates the
enabled rules that read a fact it moved. This only adds evaluations. A glucose reading
still evaluates every enabled rule, and the periodic evaluation of §5.1 every wall-clock
rule, because some facts move with time and no write (insulin and carbs on board decay,
spans and windows end, the target range and calendar gates follow the clock), and §5.1's
bound on a calendar gate holds only if every reading evaluates such a rule. Which writes
move which facts, derived ones included, is the host's record-to-fact map, not the
crate's.

### 5.3 Next change

Every evaluation (§7) reports `next_change_at`: the earliest instant strictly after `now`
at which an evaluation from the state this one left (sustained timers and tracker) can do
anything other than repeat this one's root truth and leaf log with no transition but
`continues` and no auto-resolve, if every context fact stays as it was and only `now`
advances. It is absent when no such instant exists.

It is a **lower bound**: nothing observable changes at a tick before it, and nothing need
change at it. Reporting earlier than necessary is allowed; later is a bug. It is on the
engine's time resolution, the 100 ns tick: a change that falls between two ticks is
reported at the earlier tick when that is after `now`, and at the first tick after `now`
otherwise, which is also the report when the only change due is already due (a
hysteresis window that has closed, below). So it is always strictly after `now`. An
instant past 9999-12-31T23:59:59.9999999Z, the last a .NET host can hold, is reported as
absent.

It is the minimum of the contributions of every node of the body and, while auto-resolve
is enabled and the tree is present and evaluable, every node of the auto-resolve tree
(the trees §5.2 reads), whether or not an excursion is open, plus the tracker's. A node
contributes whatever its siblings hold: a child's change moves the leaf log and timers
even when the root does not. Containers contribute only through their children, except
`sustained`.

| Node | Contribution |
|---|---|
| `signal_loss`, `staleness`, `site_age`, `sensor_age`, `tracker_age`, `loop_stale`, `loop_enaction_stale`, `time_since_last_carb`, `time_since_last_bolus` | The first instant the elapsed time since the anchor, computed exactly as evaluation computes it (§1.3), changes the comparison: where it reaches the bound for `<` and `>=`, where it passes it for `<=` and `>`, both for `==`. |
| `alert_state`, `pump_suspended`, `override_active`, `do_not_disturb`, `pump_state`, `state_span_active` with `for_minutes` | On the active side, with the span or alert present, the first instant it has held for `for_minutes`; for `pump_state` over an unrecognised mode that is where the leaf turns from false to unknown. |
| `sustained` with a timer (including one set by this evaluation) | The first instant `minutes` have passed since the timer was set, whatever its child returned; without one, only what its child contributes. |
| `threshold`, `rate_of_change`, `trend`, `glucose_bucket` with their fact present and the reading fresh (§3) | The instant the reading goes stale: where the time since its arrival passes the rule's freshness bound, so arrival `+ bound` on the tick grid, and the next tick when that is `now`. |
| `time_of_day` | The next instant the wall clock in the resolved zone (§4) reads `from` or `to`. An edge inside a DST gap is the instant the gap ends; an edge inside a fold is both instants it is read and the fold itself, where the wall clock steps back over it. |
| `day_of_week` | The next local midnight in the tenant zone, with gaps and folds as for `time_of_day`. |
| Tracker in `hysteresis` | The instant its window closes (§6.1), or, once that has passed, at once. This holds for an evaluation the rule held (§6.4) as well. |
| Tracker in `confirming` | At once: the next evaluation counts towards confirmation (§6), whatever its time. Nothing after an evaluation the rule held: the next one, with the same facts, holds too until another node's contribution changes the root. |
| Every other kind | Nothing: with the facts frozen it cannot move. `predicted` offsets are relative to the forecast's start, not to `now` (§3). |

A `signal_loss` or `staleness` leaf with no reading history measures from the rule's
`enabled_at` (§3), so it contributes `enabled_at` plus its bound like any anchor, and a host can
pre-arm that instant for a sensor that has never sent a reading. A leaf without an anchor, or
whose guard holds it constant (a `has_ever_*` flag false, a `tracker_age` threshold held back at
recording), contributes nothing, so an unknown leaf
contributes nothing (a fact arriving is not a clock event; an unknown leaf whose anchor is
known contributes as a known one: `pump_state` with `is_active` and `for_minutes` over an
unrecognised mode, §3, moves from false to unknown there), as does a
comparison that has already flipped for good (an elapsed time grows only). A stale reading
does not become fresh, so a leaf already unknown by staleness, or by an absent fact,
contributes nothing. Each flip is
found by searching the comparison evaluation makes, so the decimal rounding of §1.3 moves
it with the truth it changes: a fractional bound can hold before the exact instant it
names. A negative elapsed time (before a scheduled `tracker_age` event) truncates toward
zero, so it flips within the tick before the bound and is reported at that tick.
Confirmation (§6) counts evaluations, not time, so a rule whose tracker is `confirming`
cannot wait: its next evaluation changes it, and it reports the first tick after `now`.

A host may schedule its next evaluation of a rule at `next_change_at`, or pre-arm what that
evaluation would deliver, instead of evaluating on a timer, as long as no fact the rule
reads moves without an evaluation of the rule (§5.1). Any evaluation of the rule, by any
path, replaces the value; a rule edit drops it. The replay driver (§8) does not report it.

The backend's sweep waits for it only for a wall-clock rule that reads nothing but
`glucose` (§5.2), whose tracker is not `confirming`, and only while its context measures a
usable canonical reading and carries the glucose facts the recorded value was computed from.
A reading deleted, edited or recomputed moves those facts without an evaluation, as does an
evaluation that failed or was dropped, so the sweep then evaluates. `predictions` does not qualify: the backend
fetches them live, and they move with treatments and the clock. Nor does `time_zone`: the
backend reads it from the profile, and a profile write moves it and evaluates nothing, so a
`time_of_day`, `day_of_week` or `glucose_bucket` rule keeps the 30 s cadence. A
`glucose_bucket` also moves with the clock: the bucket comes from the target-range schedule
entry at the tenant's local time, and a schedule boundary or a profile edit changes it with no
reading. Every other wall-clock rule keeps the 30 s cadence.

A held evaluation (§6.4) records its next change like any other: the value is read from the
state the evaluation left, which is the state it found, and it is keyed by the context's glucose
facts, so a reading arriving moves the key and the sweep evaluates. A held rule still
`confirming` is never waited for, as above. A rule
never evaluated before keeps no state after a held evaluation, so nothing is recorded and the
sweep evaluates it on its cadence.

---

## 6. Excursion state machine (`ExcursionTracker`)

Per-rule persisted state: `{State, ConfirmationCount, ActiveExcursionId, UpdatedAt,
HysteresisStartedAt, AwaitingRearm}` with states `idle | confirming | active | hysteresis`. Rule inputs: `ConfirmationReadings`
(default 1), `HysteresisMinutes`, `OnUnknown` (§6.4). One evaluation whose root is known, or
read as known by `OnUnknown`, = one `ProcessEvaluationAsync(ruleId, conditionMet,
autoResolveMet)` call, where `autoResolveMet` is read only while the rule awaits re-arm
(§6.3). An evaluation the rule holds makes no such call (§6.4). **Every call**, regardless of
transition, decides `state.UpdatedAt = now`. The host persists a decision only when it changes another field: the only reader of
`UpdatedAt` is the hysteresis-start adoption below, and the decision that adopts it persists
the start. A stored state string naming none of the four states is read as
`active` when the row holds an `ActiveExcursionId`, so that excursion goes on to close,
and as `idle` otherwise, so the rule can fire again; its confirmation count, hysteresis
start and re-arm flag (§6.3) are dropped. Every operation reads it so.

| State | met | Result |
|---|---|---|
| idle, awaiting re-arm | true, and `autoResolveMet` | None (§6.3) |
| idle, awaiting re-arm | false, or not `autoResolveMet` | clear `AwaitingRearm`, then as idle below in the same call |
| idle | false | None |
| idle | true | `ConfirmationReadings <= 1` ⇒ open excursion → **active**, emit `ExcursionOpened`. Else → **confirming**, `ConfirmationCount = 1`, emit None |
| confirming | false | → **idle**, count reset, None (a single false fully resets confirmation) |
| confirming | true | `++ConfirmationCount`; if `count >= ConfirmationReadings` ⇒ open → **active**, `ExcursionOpened` (count resets to 0 on open) |
| active | true | `ExcursionContinues` |
| active | false | → **hysteresis**, `HysteresisStartedAt = now` (state and excursion row), emit `HysteresisStarted` |
| hysteresis | true | → **active**, clear `HysteresisStartedAt`, emit `HysteresisResumed` (same excursion) |
| hysteresis | false | expiry check (below). Expired ⇒ close excursion → **idle**, clear `HysteresisStartedAt`, emit `ExcursionClosed(reason: hysteresis)`; else None |

### 6.1 Hysteresis expiry

`expired = now >= HysteresisStartedAt + HysteresisMinutes`, with the window an exact
whole-minute duration. The start is the instant of the evaluation that entered hysteresis
and does not move until the excursion resumes or closes, so a window closes on the first
false evaluation at or past its end, whatever the evaluation cadence. A non-positive
`HysteresisMinutes` has always expired: the excursion closes on the first false evaluation
after the one that entered hysteresis. A window whose end is past the representable
calendar never expires. A re-entry (a true evaluation) resumes the same excursion; the
next false evaluation starts a fresh window.

State persisted before `HysteresisStartedAt` existed has none while in hysteresis. Both
engines adopt that state's `UpdatedAt` as the start, once, and persist it.

A host must also expire windows when no evaluation arrives. The backend sweep
(`CloseHysteresisWindowsAsync`, every 30 s) asks the tracker to close each excursion in
hysteresis whose window has elapsed (`CloseElapsedHysteresisAsync`), without evaluating
the condition. A window is therefore at most ~30 s late. The window's end is the
tracker's contribution to `next_change_at` (§5.3).

### 6.2 Force-close

`ForceCloseAsync(ruleId, reason)` (used by auto-resolve, manual close, rule disable): if
the tracker has an `ActiveExcursionId` (any state), close the excursion, reset to idle
(clearing `HysteresisStartedAt`), emit `ExcursionClosed(reason)`; otherwise None. The
reset sets `AwaitingRearm` exactly when the reason is `auto` and the state was `active`
(§6.3). `GetActiveExcursionIdAsync` returns the
id only in `active`/`hysteresis` states.

### 6.3 Re-arm after auto-resolve

An auto-resolve can close an excursion whose condition still holds: the resolve tree is
evaluated after the tracker on every evaluation (§7 step 5), including the one that opened
it. Without a guard the next evaluation would open a new excursion and dispatch again, and
since wall-clock rules are evaluated every 30 s against the newest reading (§5.1), that
would repeat for as long as both trees hold.

So an auto-resolve close from `active` (the last evaluation found the condition true)
leaves the rule idle with `AwaitingRearm` set. While it is set, every evaluation also
evaluates the auto-resolve tree, before the tracker, at the same `auto_resolve` root and
against the same timers as the post-tracker pass (§7 step 5), so a `sustained` inside it
keeps its first-true instant and keeps running. An evaluation that finds both the condition
and the auto-resolve tree true opens nothing. The first that finds either false clears
`AwaitingRearm` and carries on as an idle evaluation: a true condition then opens (or starts
confirming) in that same evaluation, so a relapse — the condition still holding while what
resolved it no longer does — alerts at once. An auto-resolve that is disabled, absent,
malformed or cannot be evaluated reads as false and re-arms. Only a known false re-arms: an
auto-resolve tree that is unknown keeps the hold as a true one does (the tracker's
`autoResolveMet` is then true), and an evaluation the rule holds (§6.4) keeps it whatever the
tree.

Nothing flips while both trees hold, so evaluations against one stale reading (§5.1) stay
quiet. An auto-resolve tree that flaps while the body holds re-arms on every false
evaluation, so each flip back and forth opens and dispatches again. Wrapping the tree in
`sustained` damps that, as does `ConfirmationReadings > 1` on a host that carries it.

An auto-resolve close from `hysteresis` (the last evaluation found the condition
false) does not set `AwaitingRearm`, nor does any other close reason. A manual close while
the condition holds re-opens on the next evaluation, once.

The host clears `AwaitingRearm` when a rule's condition, glucose freshness bound (§3),
auto-resolve configuration or enablement changes: the hold was taken against the rule as it
was when the auto-resolve closed it. For the same reason a host drops a tracker decision made
against a rule whose condition, glucose freshness bound, auto-resolve configuration or
enablement changed after the evaluation read it:
checked under the rule's transition lock, which the edit also takes, so an evaluation that
loaded the rule before an edit cannot set a hold the edit cleared.

State persisted before `AwaitingRearm` existed has none and reads as armed.

### 6.4 Unknown root

A rule's `OnUnknown` says what an unknown root (§2.4) means to the tracker. It is one of
three words, stored as a string; any other word is an error in the `evaluate` envelope:

| `OnUnknown` | An unknown root is |
|---|---|
| `hold` (default) | held: the tracker is not advanced |
| `met` | read as true, then exactly as a known true root |
| `not_met` | read as false, then exactly as a known false root |

A **held** evaluation makes no `ProcessEvaluationAsync` call. State, `ConfirmationCount`, the
open excursion, `HysteresisStartedAt`, `AwaitingRearm` and `UpdatedAt` stay as they were, and
the transition is `none`. The one thing that still moves is time: an excursion in hysteresis
whose window has elapsed closes, as `CloseElapsedHysteresisAsync` closes it (§6.1), with
`ExcursionClosed(reason: hysteresis)` and `UpdatedAt = now`; that window was started by a
known false and does not depend on this evaluation. A rule never evaluated before has no state
after a held evaluation. `UpdatedAt` is left alone because its only reader, the
hysteresis-start adoption of §6, reads it only in hysteresis without a start, which a held
evaluation never makes; so a state restored from what a held evaluation left evaluates as the
continuous one does.

So an open excursion whose fact disappears or goes stale (§3) stays open under `hold`,
neither continuing nor entering hysteresis, and a rule whose fact has never arrived opens
nothing; under `not_met` the first closes through hysteresis as it would on a false reading. Leaf timers follow the
`sustained` rule of §3 whatever `OnUnknown` says. Auto-resolve (§7 step 5) still runs on a
held evaluation: a known true auto-resolve tree closes the excursion, and an unknown one does
not.

---

## 7. Per-evaluation driver sequence (orchestrator contract)

For each enabled rule on each reading (after `RuleReferenceResolver.FilterEvaluable`
drops `alert_state` chains whose parents are disabled/deleted — host-side):

1. Root context: `CurrentRuleId = rule.Id`, `CurrentPath = wire(rule.ConditionType)`.
2. `conditionMet` = evaluate the rule tree, then read an unknown root through `OnUnknown`
   (§6.4).
3. `transition` = tracker.ProcessEvaluation(rule, conditionMet, autoResolveMet). Only when
   the stored state is idle awaiting re-arm, `autoResolveMet` is the auto-resolve tree
   evaluated first, exactly as in step 5, and is true unless the tree is known false;
   otherwise it is not evaluated here. A held root makes no call: the transition is `none`,
   or the hysteresis close of §6.4.
4. Open/close side effects (instances, delivery, DND suppression, info auto-ack) —
   host-side, out of crate scope.
5. **Unconditionally** (even on the same tick as an open), if `AutoResolveEnabled` and
   `AutoResolveParams` non-blank and an active excursion exists, and step 3 did not
   evaluate the tree: deserialise the auto-resolve tree; evaluate it with
   `CurrentPath = "auto_resolve"`; if true, force-close with reason `auto` (unknown does not
   resolve). A malformed
   auto-resolve JSON is skipped silently. An evaluation therefore reads the tree at most
   once; one that re-armed and opened in step 3 found it false. Consequence: a rule whose
   resolve predicate is already true when its body opens produces an open + close pair on
   the same tick, then opens nothing while both trees hold (§6.3).

6. `next_change_at` (§5.3) is read from the trees, the context, and the timers and
   tracker as steps 2 to 5 left them.

Evaluation errors in one rule are caught and logged; remaining rules still evaluate. A
rule whose tree cannot be evaluated (§1.4) is skipped before step 2, so it writes no
timers and reports no next change.

DND suppression of **delivery** (non-critical, non-`AllowThroughDnd` rules while
`ActiveDoNotDisturb != null`) is a dispatch-time decision and stays host-side; the
`do_not_disturb` *leaf* (§3) is in the crate.

---

## 8. Replay semantics (the replay driver)

Replay answers "what would these rules have done over this window": the rule set is
re-evaluated at a fixed **5-minute tick** over a historical window. The host builds each
tick's context and the driver evaluates every tick in one call — `replay::replay` in the
crate, `nocturne_alerts_replay` over the C ABI and UniFFI, `IAlertReplayEngine` in the
backend. The
machine-checkable form is `tests/Parity/AlertEngineCorpus/replay/`.

**Host side** (`AlertReplayService`; out of the driver's scope):

- Window resolution, the canonical reading stream, and per-tick enrichment as of the tick.
  Glucose is the most recent reading at or before the tick, and `LastReadingAt` is that
  reading's time, so a gap after a reading reads as stale. `LatestReceivedAt` is the instant the
  backend stored that reading, as live sends it, so past the rule's freshness bound after it
  (§3) the reading-driven leaves read unknown on the ticks live would have read them unknown. A
  reading stored after the tick it is picked for (a backfill) is fresh there, where live had not
  received it yet.
- Do Not Disturb: for each tick, the rules a fire opening on it is recorded for as
  suppressed (the gate live delivery applies on open, receipt-gated to the tick).
- Fact timelines, and mapping the driver's output to the API's events and leaf log.
- A dry-run rule (the editor's unsaved rule) carries its own `OnUnknown` and
  `glucose_freshness_minutes`; without one it takes the stored rule's when it names one, else
  `hold` and the 11-minute default, so it replays as the live engine evaluates.

**Driver**, given the rules in any order and the ticks in time order:

- **Order.** Rules are topologically sorted by `alert_state` references (parents first).
  References are read from the `alert_state` leaves of a body that can be evaluated, and
  only to rules in the set. A cycle keeps the given order for every rule. Two rules
  with one id are an error.
- **State.** One fresh timer store for the whole call; nothing is read from or written to
  persistent state. The excursion tracker is **not** consulted: confirmation and
  hysteresis do not apply, and replay's firing state is replay-local.
- **No reading.** A tick context with neither `LatestTimestamp` nor `LastReadingAt` reads
  `LastReadingAt = tick`, so a tick before the window's first reading reads as zero
  staleness and `signal_loss` cannot fire on an outage that began before the window.
  Replay does not read a rule's `enabled_at` (§3), and the backend does not send it: it is the
  rule's current value, not a history, so a window before the rule was last enabled would
  measure from an instant that did not hold then. So replay and live differ for a tenant that
  has never had a reading: live opens a `signal_loss` rule one timeout after it was enabled,
  and replay never fires it.
- **Active alerts.** One map for the whole call replaces each tick context's
  `ActiveAlerts`. A rule enters it as `ActiveAlertSnapshot("firing", tick, null)` when it
  fires and leaves it when it clears or auto-resolves, so a parent's change is visible to
  children later in the order on the same tick.
- **Per rule per tick**, in order:
  1. A rule whose body cannot be evaluated (§1.4), or whose `condition_params` is not a
     payload, is **skipped**: no leaf log, no firing change. Its `alert_state` children
     never see it fire.
  2. Root truth `met` by the normal evaluators at the rule's root path (every child
     evaluated, §2.4; shared timer store), read through the rule's `OnUnknown` (§6.4). The
     leaf log force-evaluates every leaf alone at the same root path and records each leaf's
     first observation and every flip, unknown included (`LeafTransitionPoint(atMs,
     value)`, unix ms, with `unknown` beside `value`).
  3. A rule awaiting re-arm (step 5) evaluates its enabled, evaluable auto-resolve tree at
     `auto_resolve`; when `met` is known false or the tree is, it re-arms on this tick
     (§6.3). A held `met` keeps the hold.
  4. `met && !firing` ⇒ `fired`, or `suppressed_by_dnd` when the host named the rule for
     the tick, unless the rule still awaits re-arm; the rule becomes firing either way.
     `!met && firing` ⇒ `cleared`: the rule stops firing and **all its timers are
     cleared**, the auto-resolve tree's included. An unknown `met` that the rule holds
     leaves the rule firing or not as it was and clears no timers.
  5. While firing — including on the tick it fired — and unless step 3 evaluated it, an
     enabled auto-resolve tree that parses and is evaluable is evaluated at `auto_resolve`
     (§7 step 5); true ⇒ `auto_resolved` (unknown does not resolve): the rule stops firing and awaits re-arm, and its
     timers are kept, as the tracker keeps them.
- **Output**: the evaluation order; events by tick then evaluation order; the leaf log per
  rule in evaluation order; and, on request, each rule's `met` (with `unknown` beside it
  when unknown)/`firing` (or `skipped`) after every tick. The backend's API drops `cleared`, which has no event kind there.

---

## 9. Out of scope for the crate (stays host-side)

- Context enrichment (`SensorContextEnricher` / Prelude's local assembler)
- Persistence of timers (`alert_condition_timers`), tracker state, excursions, instances
- Delivery channels, DND dispatch suppression, info auto-ack
- Sweep scheduling (30 s cadence) — including the wall-clock evaluation of §5.1 and the hysteresis expiry of §6.1
- Smart snooze policy (extend/clear, max counts, trend-favorable heuristic) — but note
  snooze *conditions* are evaluated through the normal node dispatch with
  `CurrentPath = "snooze"`, wrapped as `composite{and, [conditions]}`; that evaluation
  goes through the crate
  - The snooze context's glucose facts (`LatestValue`, `LatestTimestamp`, `TrendRate`) come
    from the newest canonical reading only when it is at most 11 minutes old; otherwise they
    are null, so `threshold`, `trend` and `glucose_bucket` leaves read unknown and the snooze
    clears. The engine's own default freshness bound (§3) is the same 11 minutes. `evaluate_node` flags an unknown value; the host's snooze treats it as false, so
    an unknown snooze condition clears the snooze.
  - The trend fallback (smart snooze on, no conditions) applies to `threshold` rules only
    and compares the newest reading (at most 11 minutes old) with the readings closest to
    5 and 10 minutes before it (each within ±2.5 minutes; both must exist), all of them
    readings with a glucose value: a sensor-error or warm-up reading is not a point. Extend when,
    in mg/dL and strictly greater: `below` — rise over 5 min > 4 or over 10 min > 10;
    `above` — fall over 5 min > 1 or over 10 min > 2 (xDrip's `trendingToAlertEnd`, on
    wall-clock spans rather than reading counts). Every other case clears. A second host
    (Prelude) must implement the same predicate; `SmartSnoozeTrendGate` is the reference.
  - `client_configuration.snooze.maxCount` (default 3) caps one count shared by manual
    snoozes and automatic extensions.
- Rule CRUD, host-specific validation (channels, tracker and alert references), `RuleReferenceResolver.FilterEvaluable`, topo-sort inputs; the condition-tree check itself is the crate's `validate` (§1.4)

## 10. Known anomalies index

| # | Behaviour | Where | Status |
|---|---|---|---|
| 1 | *(retired: a container without its children cannot be evaluated, so none is a leaf)* | §2.1 | resolved |
| 2 | *(retired: a `type` is exactly its wire string, so no path segment has another casing)* | §2.3 | resolved |
| 3 | *(retired: an unknown child kind cannot be evaluated, so no `not` inverts one)* | §1.4 | resolved |
| 4 | *(retired: `signal_loss` is an ordinary leaf evaluated on the wall clock)* | §5 | resolved |
| 5 | *(retired: hysteresis expires against a persisted `HysteresisStartedAt`, in the tracker and the sweep)* | §6.1 | resolved |
| 6 | Elapsed-time math mixes double→decimal casts (per-leaf, §1.3) | §1.3 | normative |
| 7 | `loop_enaction_stale` cold-start guard is `HasEverApsCycled`, not an enaction-specific flag: a loop that has cycled but never enacted is unknown only by its null enacted timestamp | §3 | normative |
| 8 | `time_since_last_*` treats a missing anchor as +∞ (fires on cold start, never unknown), deliberately opposite to `loop_stale`, which is unknown without its anchor | §3 | normative |
| 9 | *(retired: a `type` that is not exactly a wire string, such as a member name or ordinal, names no kind and cannot be evaluated)* | §1.2 | resolved |
| 10 | *(retired: a missing input made its leaf false, so `not` over it was true and an open excursion closed when its fact went away; a missing input is now unknown, §2.4, and an unknown root holds, §6.4)* | §3 | resolved |
