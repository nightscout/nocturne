<script lang="ts">
  import { Input } from "#lib/components/ui/input/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as ToggleGroup from "#lib/components/ui/toggle-group/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import TimezoneCombobox from "#lib/components/patient/TimezoneCombobox.svelte";
  import X from "@lucide/svelte/icons/x";
  import {
    bg,
    bgLabel,
    convertFromDisplayUnits,
    convertRateFromDisplayUnits,
    convertRateToDisplayUnits,
  } from "#lib/utils/formatting.js";
  import { conditionIssueSentence } from "./conditionIssues.svelte";
  import { alertWeekdayLabel } from "./summarizeCondition.svelte";
  import { valueStep } from "./valueStep";
  import {
    ALERT_STATES,
    alertStateLabel,
    forecastCurveLabel,
    glucoseBucketLabel,
    limitSourceLabel,
    pumpModeLabel,
    sensorStateLabel,
    stateSpanCategoryLabel,
    tempBasalMetricLabel,
    transmitterBatteryMetricLabel,
    trendBucketLabel,
    windowExtremeLabel,
  } from "./alert-labels.svelte";
  import { glucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { untrack } from "svelte";
  import DurationInput from "./DurationInput.svelte";
  import { getDefinitions } from "#lib/api/generated/trackers.generated.remote.js";
  import type { TrackerDefinitionDto } from "#lib/api/index.js";
  import {
    type ConditionNode,
    type ComparisonOperator,
    type RulePurpose,
    type StalenessOperator,
    isStringEnumMember,
    marginedPayload,
    marginPayload,
    TREND_BUCKETS,
    TempBasalMetric,
    TransmitterBatteryMetric,
    GlucoseBucket,
    PumpModeState,
    StateSpanCategory,
    AlertWeekday,
    SensorState,
    ForecastCurve,
    AlertLimitFact,
    curveChoice,
    setCurveChoice,
    type CurveChoice,
    type GlucoseLimitPayload,
    isLimitSource,
    limitSource,
    setLimitSource,
    AlertWindowExtreme,
    WINDOW_MINUTES_SHOWN,
    windowTally,
    setWindowTally,
    type WindowTally,
  } from "./types";
  import { AlertComparisonOperator } from "@nocturne/api-client";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    /**
     * Leaf condition node to edit. **Mutate fields on this node — never
     * reassign `node = somethingElse`.** Callers reach this through walks
     * like `child.not.child.sustained.child` and pass the leaf reference
     * directly; reassignment would only update the local proxy alias and
     * silently fail to splice back into the parent tree. Field mutations
     * (`node.threshold.value = …`) propagate via Svelte 5's deep proxy.
     */
    node: ConditionNode;
    availableRules?: AvailableRule[];
    purpose?: RulePurpose;
    /** The leaf is inside "for at least", which refuses a clear margin. */
    underSustained?: boolean;
  }

  let { node, availableRules = [], purpose = "notify", underSustained = false }: Props = $props();

  let margined = $derived(marginedPayload(node, purpose, underSustained));
  let storedUnderSustained = $derived(
    underSustained ? (marginPayload(node)?.clear_margin ?? 0) > 0 : false,
  );

  /** A margin as the input shows it: glucose in the display unit, a rate to its fraction. */
  function marginShown(mgdl: number): number {
    switch (node.type) {
      case "threshold":
      case "predicted":
      case "eventual_glucose":
      case "glucose_change":
      case "glucose_extreme":
        return bg(mgdl);
      case "rate_of_change":
        return convertRateToDisplayUnits(mgdl, glucoseUnits.current);
      default:
        return mgdl;
    }
  }

  /** A margin as typed, stored in mg/dL for glucose. */
  function marginStored(entered: number): number {
    switch (node.type) {
      case "threshold":
      case "predicted":
      case "eventual_glucose":
      case "glucose_change":
      case "glucose_extreme":
        return convertFromDisplayUnits(entered, glucoseUnits.current);
      case "rate_of_change":
        return convertRateFromDisplayUnits(entered, glucoseUnits.current);
      default:
        return entered;
    }
  }

  // A mounted leaf editor's kind never changes (the picker adds new leaves
  // rather than retyping existing ones), so the query can be created
  // conditionally at init — same pattern as ClockFaceRenderer. untrack makes
  // the intentional initial-value read explicit.
  const definitionsQuery = untrack(() => node.type) === "tracker_age" ? getDefinitions({}) : null;
  const trackerDefinitions = $derived<TrackerDefinitionDto[]>(
    definitionsQuery?.current ?? [],
  );

  /** A sensor state's start is known only for No value, so only it can be timed. */
  function onlyNoValue(states: SensorState[] | undefined): boolean {
    return states?.length === 1 && states[0] === SensorState.NoValue;
  }

  const NAMED_CURVES: readonly ForecastCurve[] = [
    ForecastCurve.Iob,
    ForecastCurve.Cob,
    ForecastCurve.Uam,
    ForecastCurve.Zt,
  ];

  function curveChoiceLabel(choice: CurveChoice): string {
    switch (choice) {
      case "curve":
        return "one curve";
      case "min_of":
        return "lowest of";
      case "max_of":
        return "highest of";
    }
  }

  /** A threshold compares the latest glucose, so it cannot compare with it again. */
  const THRESHOLD_LIMIT_FACTS: readonly AlertLimitFact[] = [
    AlertLimitFact.TargetLow,
    AlertLimitFact.TargetHigh,
  ];
  const LIMIT_FACTS: readonly AlertLimitFact[] = [
    AlertLimitFact.TargetLow,
    AlertLimitFact.TargetHigh,
    AlertLimitFact.LatestGlucose,
  ];

  function windowTallyLabel(tally: WindowTally): string {
    switch (tally) {
      case "count":
        return "Number";
      case "percent":
        return "Percent";
    }
  }

  function isWindowTally(value: string): value is WindowTally {
    return value === "count" || value === "percent";
  }

  function isCurveChoice(value: string): value is CurveChoice {
    return value === "curve" || value === "min_of" || value === "max_of";
  }

  // Stored on the wire as the symbol; shown in words.
  const OPERATORS: readonly ComparisonOperator[] = [">=", ">", "<=", "<"];
  const STALENESS_OPERATORS: readonly StalenessOperator[] = [">=", ">"];

  /** Built per call so wuchale reads it in the current locale. */
  function operatorLabel(operator: string): string {
    switch (operator) {
      case ">=":
        return "at least";
      case ">":
        return "more than";
      case "<=":
        return "at most";
      case "<":
        return "less than";
      default:
        return operator;
    }
  }
  // PumpMode rules use the dedicated pump_state condition (the backend rejects
  // PumpMode on the generic state-span condition at validation time).
  const stateCategories = Object.values(StateSpanCategory).filter(
    (category) => category !== StateSpanCategory.PumpMode && category !== StateSpanCategory.Motion,
  );
  // Mon → Sun. Order is fixed to a Monday-start week for the toggle pills.
  const weekdayOrder: AlertWeekday[] = [
    AlertWeekday.Monday,
    AlertWeekday.Tuesday,
    AlertWeekday.Wednesday,
    AlertWeekday.Thursday,
    AlertWeekday.Friday,
    AlertWeekday.Saturday,
    AlertWeekday.Sunday,
  ];

  function isWeekday(value: string): value is AlertWeekday {
    return Object.values(AlertWeekday).some((day) => day === value);
  }

  function parseNumber(value: string, fallback: number): number {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }

  function leafSuffix(kind: string): string {
    switch (kind) {
      case "iob":
      case "reservoir":
        return "U";
      case "cob":
      case "carbs_required":
        return "g";
      case "pump_battery":
      case "uploader_battery":
        return "%";
      default:
        return "";
    }
  }
</script>

<!--
  Inline editor for a single leaf condition. Renders to fit inside a row pill —
  small inputs side-by-side, no labels, suffixes inline. Each branch reads the
  node's payload via the discriminator and binds back through the same key, so
  Svelte 5's deep-proxy state propagates the mutation up the tree.
-->
{#snippet glucoseLimit(p: GlucoseLimitPayload, facts: readonly AlertLimitFact[])}
  {@const source = limitSource(p)}
  <Select.Root
    type="single"
    value={source}
    onValueChange={(v) => {
      if (isLimitSource(v)) setLimitSource(p, v, 70);
    }}
  >
    <Select.Trigger size="sm" class="w-32" aria-label="Compare with">
      {limitSourceLabel(source)}
    </Select.Trigger>
    <Select.Content>
      <Select.Item value="value" label={limitSourceLabel("value")} />
      {#each facts as fact (fact)}
        <Select.Item value={fact} label={limitSourceLabel(fact)} />
      {/each}
    </Select.Content>
  </Select.Root>
  {#if p.value_from}
    {@const reference = p.value_from}
    <span class="text-xs text-muted-foreground">plus</span>
    <Input
      type="number"
      aria-label="Offset"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={bg(reference.offset ?? 0)}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        reference.offset = convertFromDisplayUnits(
          parseNumber(e.currentTarget.value, bg(reference.offset ?? 0)),
          glucoseUnits.current,
        );
      }}
    />
  {:else}
    <Input
      type="number"
      aria-label="Value"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={bg(p.value ?? 0)}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        p.value = convertFromDisplayUnits(
          parseNumber(e.currentTarget.value, bg(p.value ?? 0)),
          glucoseUnits.current,
        );
      }}
    />
  {/if}
{/snippet}

{#snippet targetNote(p: GlucoseLimitPayload)}
  {#if p.value_from && p.value_from.fact !== AlertLimitFact.LatestGlucose}
    <span class="w-full text-xs text-muted-foreground">
      If your profile has no target range, this condition does not know if it is true.
    </span>
  {/if}
{/snippet}

{#snippet comparison(p: { operator?: string }, label: string)}
  <Select.Root
    type="single"
    value={p.operator ?? ">="}
    onValueChange={(v) => {
      p.operator = v;
    }}
  >
    <Select.Trigger size="sm" class="w-28" aria-label={label}>
      {operatorLabel(p.operator ?? ">=")}
    </Select.Trigger>
    <Select.Content>
      {#each OPERATORS as op (op)}
        <Select.Item value={op} label={operatorLabel(op)} />
      {/each}
    </Select.Content>
  </Select.Root>
{/snippet}

{#snippet glucoseValue(p: { value?: number }, label: string)}
  <Input
    type="number"
    aria-label={label}
    step={valueStep(node.type)}
    size="sm"
    class="w-20 text-right tabular-nums"
    value={bg(p.value ?? 0)}
    oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
      p.value = convertFromDisplayUnits(
        parseNumber(e.currentTarget.value, bg(p.value ?? 0)),
        glucoseUnits.current,
      );
    }}
  />
{/snippet}

{#snippet windowMinutes(p: { window_minutes?: number })}
  <span class="text-xs text-muted-foreground">in</span>
  <Input
    type="number"
    aria-label="Window minutes"
    min="5"
    max="180"
    step="1"
    size="sm"
    class="w-16 text-right tabular-nums"
    value={p.window_minutes ?? WINDOW_MINUTES_SHOWN}
    oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
      p.window_minutes = parseNumber(e.currentTarget.value, p.window_minutes ?? WINDOW_MINUTES_SHOWN);
    }}
  />
  <span class="text-xs text-muted-foreground">min</span>
{/snippet}

{#snippet windowNote()}
  <span class="w-full text-xs text-muted-foreground">The time window ends at the newest reading.</span>
{/snippet}

<div class="flex flex-wrap items-center gap-1.5">
  {#if node.type === "threshold" && node.threshold}
    {@render comparison(node.threshold, "Comparison")}
    {@render glucoseLimit(node.threshold, THRESHOLD_LIMIT_FACTS)}
    <span class="text-xs text-muted-foreground">{bgLabel()}</span>
    {@render targetNote(node.threshold)}
  {:else if node.type === "predicted" && node.predicted}
    <Select.Root
      type="single"
      value={node.predicted.operator ?? "<="}
      onValueChange={(v) => {
        if (node.predicted) node.predicted.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(node.predicted.operator ?? "<=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    {@render glucoseLimit(node.predicted, LIMIT_FACTS)}
    <span class="text-xs text-muted-foreground">{bgLabel()} within</span>
    <Input
      type="number"
      aria-label="Minutes"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      value={node.predicted.within_minutes ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.predicted)
          node.predicted.within_minutes = parseNumber(
            e.currentTarget.value,
            node.predicted.within_minutes ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
    {@const predicted = node.predicted}
    {@const choice = curveChoice(predicted)}
    {#if choice}
      <span class="text-xs text-muted-foreground">from</span>
      <Select.Root
        type="single"
        value={choice}
        onValueChange={(v) => {
          if (isCurveChoice(v)) setCurveChoice(predicted, v);
        }}
      >
        <Select.Trigger size="sm" class="w-32" aria-label="Forecast choice">
          {curveChoiceLabel(choice)}
        </Select.Trigger>
        <Select.Content>
          {#each ["curve", "min_of", "max_of"] as const as option (option)}
            <Select.Item value={option} label={curveChoiceLabel(option)} />
          {/each}
        </Select.Content>
      </Select.Root>
      {#if choice === "curve"}
        <Select.Root
          type="single"
          value={predicted.curve ?? ForecastCurve.Iob}
          onValueChange={(v) => {
            if (isStringEnumMember(ForecastCurve, v)) predicted.curve = v;
          }}
        >
          <Select.Trigger size="sm" class="w-44" aria-label="Forecast curve">
            {forecastCurveLabel(predicted.curve ?? ForecastCurve.Iob)}
          </Select.Trigger>
          <Select.Content>
            {#each NAMED_CURVES as curve (curve)}
              <Select.Item value={curve} label={forecastCurveLabel(curve)} />
            {/each}
          </Select.Content>
        </Select.Root>
      {:else}
        {@const listed = (choice === "min_of" ? predicted.min_of : predicted.max_of) ?? []}
        <ToggleGroup.Root
          type="multiple"
          size="sm"
          spacing={1}
          value={[...listed]}
          onValueChange={(v: string[]) => {
            const curves = v.filter((c) => isStringEnumMember(ForecastCurve, c));
            if (curves.length === 0) return;
            if (choice === "min_of") predicted.min_of = curves;
            else predicted.max_of = curves;
          }}
          class="flex-wrap"
        >
          {#each NAMED_CURVES as curve (curve)}
            <ToggleGroup.Item value={curve} aria-label={forecastCurveLabel(curve)}>
              {forecastCurveLabel(curve)}
            </ToggleGroup.Item>
          {/each}
        </ToggleGroup.Root>
      {/if}
      <span class="w-full text-xs text-muted-foreground">
        If the loop does not send a curve that you select, the rule waits and shows the cause.
      </span>
    {/if}
    {@render targetNote(predicted)}
  {:else if node.type === "eventual_glucose" && node.eventual_glucose}
    {@const eventual = node.eventual_glucose}
    <Select.Root
      type="single"
      value={eventual.operator ?? "<"}
      onValueChange={(v) => {
        eventual.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(eventual.operator ?? "<")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    {@render glucoseLimit(eventual, LIMIT_FACTS)}
    <span class="text-xs text-muted-foreground">{bgLabel()}</span>
    {@render targetNote(eventual)}
  {:else if node.type === "rate_of_change" && node.rate_of_change}
    {@const rate = node.rate_of_change}
    {@render comparison(rate, "Comparison")}
    <Input
      type="number"
      aria-label="Value"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={convertRateToDisplayUnits(rate.value ?? 0, glucoseUnits.current)}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        rate.value = convertRateFromDisplayUnits(
          parseNumber(
            e.currentTarget.value,
            convertRateToDisplayUnits(rate.value ?? 0, glucoseUnits.current),
          ),
          glucoseUnits.current,
        );
      }}
    />
    <span class="text-xs text-muted-foreground">{bgLabel()} per minute</span>
    <span class="w-full text-xs text-muted-foreground">A fall is a negative number.</span>
  {:else if node.type === "glucose_change" && node.glucose_change}
    {@const change = node.glucose_change}
    {@render comparison(change, "Comparison")}
    {@render glucoseValue(change, "Value")}
    <span class="text-xs text-muted-foreground">{bgLabel()}</span>
    {@render windowMinutes(change)}
    <span class="w-full text-xs text-muted-foreground">
      The time window ends at the newest reading. The change is the newest reading minus the oldest
      reading in the window. A fall is a negative number.
    </span>
  {:else if node.type === "glucose_extreme" && node.glucose_extreme}
    {@const extreme = node.glucose_extreme}
    <Select.Root
      type="single"
      value={extreme.of ?? AlertWindowExtreme.Min}
      onValueChange={(v) => {
        if (isStringEnumMember(AlertWindowExtreme, v)) extreme.of = v;
      }}
    >
      <Select.Trigger size="sm" class="w-24" aria-label="Lowest or highest">
        {windowExtremeLabel(extreme.of ?? AlertWindowExtreme.Min)}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(AlertWindowExtreme) as of (of)}
          <Select.Item value={of} label={windowExtremeLabel(of)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">glucose</span>
    {@render windowMinutes(extreme)}
    {@render comparison(extreme, "Comparison")}
    {@render glucoseValue(extreme, "Value")}
    <span class="text-xs text-muted-foreground">{bgLabel()}</span>
    {@render windowNote()}
  {:else if node.type === "glucose_count" && node.glucose_count}
    {@const count = node.glucose_count}
    {@const tally = windowTally(count)}
    {@const compared = tally === "percent" ? count.percent : count.count}
    <Select.Root
      type="single"
      value={tally}
      onValueChange={(v) => {
        if (isWindowTally(v)) setWindowTally(count, v);
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Number or percent">
        {windowTallyLabel(tally)}
      </Select.Trigger>
      <Select.Content>
        {#each ["count", "percent"] as const as option (option)}
          <Select.Item value={option} label={windowTallyLabel(option)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">of readings</span>
    {#if count.reading}
      {@render comparison(count.reading, "Reading comparison")}
      {@render glucoseValue(count.reading, "Reading value")}
      <span class="text-xs text-muted-foreground">{bgLabel()}</span>
    {/if}
    {@render windowMinutes(count)}
    {#if compared}
      <span class="text-xs text-muted-foreground">is</span>
      {@render comparison(compared, "Comparison")}
      <Input
        type="number"
        aria-label={tally === "percent" ? "Percent" : "Number of readings"}
        min="0"
        max={tally === "percent" ? "100" : undefined}
        step="1"
        size="sm"
        class="w-16 text-right tabular-nums"
        value={compared.value ?? 0}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
          compared.value = parseNumber(e.currentTarget.value, compared.value ?? 0);
        }}
      />
      <span class="text-xs text-muted-foreground">{tally === "percent" ? "%" : "readings"}</span>
    {/if}
    {@render windowNote()}
  {:else if node.type === "trend" && node.trend}
    <Select.Root
      type="single"
      value={node.trend.bucket ?? "falling"}
      onValueChange={(v) => {
        if (node.trend) node.trend.bucket = v;
      }}
    >
      <Select.Trigger size="sm" class="w-32" aria-label="Trend">
        {trendBucketLabel(node.trend.bucket ?? "falling")}
      </Select.Trigger>
      <Select.Content>
        {#each TREND_BUCKETS as bucket (bucket)}
          <Select.Item value={bucket} label={trendBucketLabel(bucket)} />
        {/each}
      </Select.Content>
    </Select.Root>
  {:else if (node.type === "staleness" || node.type === "site_age" || node.type === "sensor_age") && node[node.type]}
    {@const payload = node[node.type]!}
    {@render comparison(payload, "Comparison")}
    <DurationInput
      label="Duration"
      minutes={payload.minutes}
      onChange={(minutes) => {
        payload.minutes = minutes;
      }}
    />
  {:else if node.type === "time_of_day" && node.time_of_day}
    <Input
      type="time"
      aria-label="From"
      size="sm"
      class="w-24 tabular-nums"
      value={node.time_of_day.from ?? "00:00"}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.time_of_day) node.time_of_day.from = e.currentTarget.value;
      }}
    />
    <span class="text-xs text-muted-foreground">–</span>
    <Input
      type="time"
      aria-label="To"
      size="sm"
      class="w-24 tabular-nums"
      value={node.time_of_day.to ?? "23:59"}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.time_of_day) node.time_of_day.to = e.currentTarget.value;
      }}
    />
    <TimezoneCombobox
      class="w-44"
      placeholder="Profile time zone"
      value={node.time_of_day.timezone ?? undefined}
      onValueChange={(zone) => {
        if (node.time_of_day) node.time_of_day.timezone = zone;
      }}
    />
    {#if node.time_of_day.timezone}
      <Button
        type="button"
        variant="ghost"
        size="icon-xs"
        aria-label="Use the profile time zone"
        onclick={() => {
          if (node.time_of_day) node.time_of_day.timezone = undefined;
        }}
      >
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  {:else if (node.type === "iob" || node.type === "cob" || node.type === "reservoir" || node.type === "pump_battery" || node.type === "uploader_battery" || node.type === "sensitivity_ratio" || node.type === "carbs_required") && node[node.type]}
    {@const payload = node[node.type]!}
    {@const suffix = leafSuffix(node.type)}
    <Select.Root
      type="single"
      value={payload.operator ?? ">="}
      onValueChange={(v) => {
        payload.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(payload.operator ?? ">=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Value"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={payload.value ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        payload.value = parseNumber(e.currentTarget.value, payload.value ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">{suffix}</span>
  {:else if (node.type === "loop_stale" || node.type === "loop_enaction_stale") && node[node.type]}
    {@const payload = node[node.type]!}
    <Select.Root
      type="single"
      value={payload.operator ?? ">"}
      onValueChange={(v) => {
        payload.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(payload.operator ?? ">")}
      </Select.Trigger>
      <Select.Content>
        {#each STALENESS_OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Minutes"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      value={payload.minutes ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        payload.minutes = parseNumber(e.currentTarget.value, payload.minutes ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "signal_loss" && node.signal_loss}
    <span class="text-xs text-muted-foreground">no data for at least</span>
    <Input
      type="number"
      aria-label="Minutes"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      value={node.signal_loss.timeout_minutes ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.signal_loss)
          node.signal_loss.timeout_minutes = parseNumber(
            e.currentTarget.value,
            node.signal_loss.timeout_minutes ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "temp_basal" && node.temp_basal}
    <Select.Root
      type="single"
      value={node.temp_basal.metric ?? TempBasalMetric.Rate}
      onValueChange={(v) => {
        if (node.temp_basal && isStringEnumMember(TempBasalMetric, v)) node.temp_basal.metric = v;
      }}
    >
      <Select.Trigger size="sm" class="w-32" aria-label="Measure">
        {tempBasalMetricLabel(node.temp_basal.metric ?? TempBasalMetric.Rate)}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(TempBasalMetric) as metric (metric)}
          <Select.Item value={metric} label={tempBasalMetricLabel(metric)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Select.Root
      type="single"
      value={node.temp_basal.operator ?? ">="}
      onValueChange={(v) => {
        if (node.temp_basal) node.temp_basal.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(node.temp_basal.operator ?? ">=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Value"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={node.temp_basal.value ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.temp_basal)
          node.temp_basal.value = parseNumber(
            e.currentTarget.value,
            node.temp_basal.value ?? 0,
          );
      }}
    />
  {:else if node.type === "transmitter_battery" && node.transmitter_battery}
    {@const payload = node.transmitter_battery}
    <Select.Root
      type="single"
      value={payload.metric ?? TransmitterBatteryMetric.VoltageA}
      onValueChange={(v) => {
        if (isStringEnumMember(TransmitterBatteryMetric, v)) payload.metric = v;
      }}
    >
      <Select.Trigger size="sm" class="w-32" aria-label="Measure">
        {transmitterBatteryMetricLabel(payload.metric ?? TransmitterBatteryMetric.VoltageA)}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(TransmitterBatteryMetric) as metric (metric)}
          <Select.Item value={metric} label={transmitterBatteryMetricLabel(metric)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Select.Root
      type="single"
      value={payload.operator ?? ">="}
      onValueChange={(v) => {
        payload.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(payload.operator ?? ">=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Value"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={payload.value ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        payload.value = parseNumber(e.currentTarget.value, payload.value ?? 0);
      }}
    />
  {:else if (node.type === "pump_suspended" || node.type === "override_active" || node.type === "do_not_disturb") && node[node.type]}
    {@const payload = node[node.type]!}
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      aria-label="Active"
      checked={payload.is_active ?? false}
      onCheckedChange={(checked: boolean) => {
        payload.is_active = checked;
        if (!checked) payload.for_minutes = undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">{payload.is_active ? "active" : "inactive"}</span>
    {#if payload.is_active}
      <span class="text-xs text-muted-foreground">for at least</span>
      <Input
        type="number"
      aria-label="Minutes, optional"
        min="1"
        size="sm"
        class="w-16 text-right tabular-nums"
        placeholder="any"
        value={payload.for_minutes ?? ""}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
          const v = e.currentTarget.value;
          payload.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
        }}
      />
      <span class="text-xs text-muted-foreground">min</span>
    {/if}
  {:else if node.type === "alert_state" && node.alert_state}
    {@const selectedRule = availableRules.find((r) => r.id === node.alert_state?.alert_id)}
    <Select.Root
      type="single"
      value={node.alert_state.alert_id ?? ""}
      onValueChange={(v) => {
        if (node.alert_state) node.alert_state.alert_id = v;
      }}
    >
      <Select.Trigger size="sm" class="w-44" aria-label="Alert">
        {selectedRule?.name ?? "Select an alert"}
      </Select.Trigger>
      <Select.Content>
        {#each availableRules as rule (rule.id)}
          <Select.Item value={rule.id} label={rule.name} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">is</span>
    <Select.Root
      type="single"
      value={node.alert_state.state ?? "firing"}
      onValueChange={(v) => {
        if (node.alert_state) node.alert_state.state = v;
      }}
    >
      <Select.Trigger size="sm" class="w-32" aria-label="State">
        {alertStateLabel(node.alert_state.state ?? "firing")}
      </Select.Trigger>
      <Select.Content>
        {#each ALERT_STATES as state (state)}
          <Select.Item value={state} label={alertStateLabel(state)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">for at least</span>
    <Input
      type="number"
      aria-label="Minutes, optional"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      placeholder="any"
      value={node.alert_state.for_minutes ?? ""}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        const v = e.currentTarget.value;
        if (node.alert_state)
          node.alert_state.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "glucose_bucket" && node.glucose_bucket}
    {@const selected = new Set(node.glucose_bucket.buckets ?? [])}
    <ToggleGroup.Root
      type="multiple"
      size="sm"
      spacing={1}
      value={[...selected]}
      onValueChange={(v: string[]) => {
        if (node.glucose_bucket)
          node.glucose_bucket.buckets = v.filter((b) => isStringEnumMember(GlucoseBucket, b));
      }}
      class="flex-wrap"
    >
      {#each Object.values(GlucoseBucket) as bucket (bucket)}
        <ToggleGroup.Item value={bucket} aria-label={glucoseBucketLabel(bucket)}>
          {glucoseBucketLabel(bucket)}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  {:else if (node.type === "time_since_last_carb" || node.type === "time_since_last_bolus") && node[node.type]}
    {@const payload = node[node.type]!}
    <Select.Root
      type="single"
      value={payload.operator ?? ">="}
      onValueChange={(v) => {
        if (isStringEnumMember(AlertComparisonOperator, v)) payload.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(payload.operator ?? ">=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Minutes"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      value={payload.minutes ?? 0}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        payload.minutes = parseNumber(e.currentTarget.value, payload.minutes ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "day_of_week" && node.day_of_week}
    {@const selectedDays = new Set(node.day_of_week.days ?? [])}
    <ToggleGroup.Root
      type="multiple"
      size="sm"
      spacing={1}
      value={[...selectedDays]}
      onValueChange={(v: string[]) => {
        if (node.day_of_week)
          node.day_of_week.days = v.filter(isWeekday);
      }}
      class="flex-wrap"
    >
      {#each weekdayOrder as day (day)}
        <ToggleGroup.Item value={day} aria-label={alertWeekdayLabel(day)}>
          {alertWeekdayLabel(day)}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  {:else if node.type === "sensor_state" && node.sensor_state}
    {@const selectedStates = new Set(node.sensor_state.states ?? [])}
    {@const timed = onlyNoValue(node.sensor_state.states)}
    <ToggleGroup.Root
      type="multiple"
      size="sm"
      spacing={1}
      value={[...selectedStates]}
      onValueChange={(v: string[]) => {
        if (!node.sensor_state) return;
        node.sensor_state.states = v.filter((s) => isStringEnumMember(SensorState, s));
        if (!onlyNoValue(node.sensor_state.states)) node.sensor_state.for_minutes = undefined;
      }}
      class="flex-wrap"
    >
      {#each Object.values(SensorState) as state (state)}
        <ToggleGroup.Item value={state} aria-label={sensorStateLabel(state)}>
          {sensorStateLabel(state)}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
    <span class="text-xs text-muted-foreground">for at least</span>
    <Input
      type="number"
      aria-label="Minutes, optional"
      min="1"
      size="sm"
      class="w-16 text-right tabular-nums"
      placeholder="any"
      disabled={!timed}
      value={node.sensor_state.for_minutes ?? ""}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        const v = e.currentTarget.value;
        if (node.sensor_state)
          node.sensor_state.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
    {#if !timed}
      <span class="text-xs text-muted-foreground">Only for No value. The other states have no start time.</span>
    {/if}
  {:else if node.type === "pump_state" && node.pump_state}
    <Select.Root
      type="single"
      value={node.pump_state.mode ?? PumpModeState.Suspended}
      onValueChange={(v) => {
        if (node.pump_state && isStringEnumMember(PumpModeState, v)) node.pump_state.mode = v;
      }}
    >
      <Select.Trigger size="sm" class="w-32" aria-label="Pump mode">
        {pumpModeLabel(node.pump_state.mode ?? PumpModeState.Suspended)}
      </Select.Trigger>
      <Select.Content>
        {#each Object.values(PumpModeState) as mode (mode)}
          <Select.Item value={mode} label={pumpModeLabel(mode)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      aria-label="Active"
      checked={node.pump_state.is_active ?? false}
      onCheckedChange={(checked: boolean) => {
        if (!node.pump_state) return;
        node.pump_state.is_active = checked;
        if (!checked) node.pump_state.for_minutes = undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">{node.pump_state.is_active ? "active" : "inactive"}</span>
    {#if node.pump_state.is_active}
      <span class="text-xs text-muted-foreground">for at least</span>
      <Input
        type="number"
      aria-label="Minutes, optional"
        min="1"
        size="sm"
        class="w-16 text-right tabular-nums"
        placeholder="any"
        value={node.pump_state.for_minutes ?? ""}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
          if (!node.pump_state) return;
          const v = e.currentTarget.value;
          node.pump_state.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
        }}
      />
      <span class="text-xs text-muted-foreground">min</span>
    {/if}
  {:else if node.type === "state_span_active" && node.state_span_active}
    <Select.Root
      type="single"
      value={node.state_span_active.category ?? StateSpanCategory.Override}
      onValueChange={(v) => {
        if (node.state_span_active && isStringEnumMember(StateSpanCategory, v))
          node.state_span_active.category = v;
      }}
    >
      <Select.Trigger size="sm" class="w-40" aria-label="Category">
        {stateSpanCategoryLabel(node.state_span_active.category ?? StateSpanCategory.Override)}
      </Select.Trigger>
      <Select.Content>
        {#each stateCategories as category (category)}
          <Select.Item value={category} label={stateSpanCategoryLabel(category)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="text"
      aria-label="State"
      size="sm"
      class="w-28"
      placeholder="any state"
      value={node.state_span_active.state ?? ""}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (!node.state_span_active) return;
        const v = e.currentTarget.value;
        node.state_span_active.state = v.length > 0 ? v : undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      aria-label="Active"
      checked={node.state_span_active.is_active ?? false}
      onCheckedChange={(checked: boolean) => {
        if (!node.state_span_active) return;
        node.state_span_active.is_active = checked;
        if (!checked) node.state_span_active.for_minutes = undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">{node.state_span_active.is_active ? "active" : "inactive"}</span>
    {#if node.state_span_active.is_active}
      <span class="text-xs text-muted-foreground">for at least</span>
      <Input
        type="number"
      aria-label="Minutes, optional"
        min="1"
        size="sm"
        class="w-16 text-right tabular-nums"
        placeholder="any"
        value={node.state_span_active.for_minutes ?? ""}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
          if (!node.state_span_active) return;
          const v = e.currentTarget.value;
          node.state_span_active.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
        }}
      />
      <span class="text-xs text-muted-foreground">min</span>
    {/if}
  {:else if node.type === "sleep_session_active" && node.sleep_session_active}
    <span class="text-xs text-muted-foreground">sleep session is</span>
    <Switch
      aria-label="Active"
      checked={node.sleep_session_active.is_active ?? false}
      onCheckedChange={(checked) => {
        if (node.sleep_session_active)
          node.sleep_session_active.is_active = checked;
      }}
    />
    <span class="text-xs text-muted-foreground">{node.sleep_session_active.is_active ? "active" : "inactive"}</span>
  {:else if node.type === "tracker_age" && node.tracker_age}
    {@const selectedDef = trackerDefinitions.find(
      (d) => d.id === node.tracker_age?.tracker_definition_id,
    )}
    <Select.Root
      type="single"
      value={node.tracker_age.tracker_definition_id ?? ""}
      onValueChange={(v) => {
        if (node.tracker_age) node.tracker_age.tracker_definition_id = v;
      }}
    >
      <Select.Trigger size="sm" class="w-44" aria-label="Tracker">
        {selectedDef?.name ?? "Select a tracker"}
      </Select.Trigger>
      <Select.Content>
        {#each trackerDefinitions as def (def.id)}
          <Select.Item value={def.id ?? ""} label={def.name ?? ""} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Select.Root
      type="single"
      value={node.tracker_age.operator ?? ">="}
      onValueChange={(v) => {
        if (node.tracker_age) node.tracker_age.operator = v;
      }}
    >
      <Select.Trigger size="sm" class="w-28" aria-label="Comparison">
        {operatorLabel(node.tracker_age.operator ?? ">=")}
      </Select.Trigger>
      <Select.Content>
        {#each OPERATORS as op (op)}
          <Select.Item value={op} label={operatorLabel(op)} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      aria-label="Hours"
      step="1"
      size="sm"
      class="w-20 text-right tabular-nums"
      value={(node.tracker_age.minutes ?? 0) / 60}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        if (node.tracker_age)
          node.tracker_age.minutes = Math.round(
            parseNumber(
              e.currentTarget.value,
              (node.tracker_age.minutes ?? 0) / 60,
            ) * 60,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">
      hours. Use a negative number for the time before a scheduled event.
    </span>
  {/if}
  {#if margined && margined.clear_margin != null}
    {@const payload = margined}
    <span class="text-xs text-muted-foreground">clear margin</span>
    <Input
      type="number"
      aria-label="Clear margin"
      min="0"
      step={valueStep(node.type)}
      size="sm"
      class="w-20 text-right tabular-nums"
      value={marginShown(payload.clear_margin ?? 0)}
      oninput={(e: Event & { currentTarget: HTMLInputElement }) => {
        const entered = Math.max(
          0,
          parseNumber(e.currentTarget.value, marginShown(payload.clear_margin ?? 0)),
        );
        payload.clear_margin = marginStored(entered);
      }}
    />
    <span class="text-xs text-muted-foreground">
      {node.type === "rate_of_change"
        ? `${bgLabel()} per minute`
        : node.type === "threshold" ||
            node.type === "predicted" ||
            node.type === "eventual_glucose" ||
            node.type === "glucose_change" ||
            node.type === "glucose_extreme"
          ? bgLabel()
          : leafSuffix(node.type)}
    </span>
  {/if}
  {#if storedUnderSustained}
    <span class="text-xs text-destructive">
      {conditionIssueSentence("margin_under_sustained", "clear_margin")}
    </span>
    <Button
      type="button"
      variant="ghost"
      size="sm"
      onclick={() => {
        const payload = marginPayload(node);
        if (payload) payload.clear_margin = undefined;
      }}
    >
      Remove the clear margin
    </Button>
  {/if}
</div>

