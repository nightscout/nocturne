<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import * as Select from "$lib/components/ui/select";
  import * as ToggleGroup from "$lib/components/ui/toggle-group";
  import { Switch } from "$lib/components/ui/switch";
  import {
    type ConditionNode,
    type ComparisonOperator,
    type StalenessOperator,
    type TrendBucket,
    type TimeSinceLastCarbCondition,
    type TimeSinceLastBolusCondition,
    TempBasalMetric,
    GlucoseBucket,
    PumpModeState,
    StateSpanCategory,
    DayOfWeek,
  } from "./types";

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
  }

  let { node, availableRules = [] }: Props = $props();

  // Display strings for operators and trend buckets. Stored on the wire as the
  // literal symbol/key; formatted symbols (≥, ≤) are display-only.
  const opLabels: Record<ComparisonOperator, string> = {
    ">=": "≥",
    ">": ">",
    "<=": "≤",
    "<": "<",
  };
  const stalenessOpLabels: Record<StalenessOperator, string> = {
    ">=": "≥",
    ">": ">",
  };
  const trendLabels: Record<TrendBucket, string> = {
    falling_fast: "falling fast",
    falling: "falling",
    flat: "flat",
    rising: "rising",
    rising_fast: "rising fast",
  };
  const tempBasalMetricLabels: Record<TempBasalMetric, string> = {
    [TempBasalMetric.Rate]: "rate (U/h)",
    [TempBasalMetric.PercentOfScheduled]: "% of scheduled",
  };
  const glucoseBucketLabels: Record<GlucoseBucket, string> = {
    [GlucoseBucket.VeryLow]: "very low",
    [GlucoseBucket.Low]: "low",
    [GlucoseBucket.TightRange]: "tight range",
    [GlucoseBucket.InRange]: "in range",
    [GlucoseBucket.High]: "high",
    [GlucoseBucket.VeryHigh]: "very high",
  };
  const pumpModeLabels: Record<PumpModeState, string> = {
    [PumpModeState.Automatic]: "Automatic",
    [PumpModeState.Limited]: "Limited",
    [PumpModeState.Manual]: "Manual",
    [PumpModeState.Boost]: "Boost",
    [PumpModeState.EaseOff]: "Ease off",
    [PumpModeState.Sleep]: "Sleep",
    [PumpModeState.Exercise]: "Exercise",
    [PumpModeState.Suspended]: "Suspended",
    [PumpModeState.Off]: "Off",
  };
  // PumpMode is intentionally excluded — pump mode rules use the dedicated
  // pump_state condition (the backend rejects PumpMode on the generic state-span
  // condition at validation time).
  const stateCategoryLabels: Partial<Record<StateSpanCategory, string>> = {
    [StateSpanCategory.PumpConnectivity]: "Pump connectivity",
    [StateSpanCategory.Override]: "Override",
    [StateSpanCategory.Profile]: "Profile",
    [StateSpanCategory.Sleep]: "Sleep",
    [StateSpanCategory.Exercise]: "Exercise",
    [StateSpanCategory.Illness]: "Illness",
    [StateSpanCategory.Travel]: "Travel",
    [StateSpanCategory.DataExclusion]: "Data exclusion",
    [StateSpanCategory.TemporaryTarget]: "Temporary target",
  };
  // Mon → Sun. Order is fixed to a Monday-start week for the toggle pills; the
  // wire stores System.DayOfWeek values regardless of display order.
  const weekdayOrder: DayOfWeek[] = [
    DayOfWeek.Monday,
    DayOfWeek.Tuesday,
    DayOfWeek.Wednesday,
    DayOfWeek.Thursday,
    DayOfWeek.Friday,
    DayOfWeek.Saturday,
    DayOfWeek.Sunday,
  ];
  const weekdayLabels: Record<DayOfWeek, string> = {
    [DayOfWeek.Sunday]: "Sun",
    [DayOfWeek.Monday]: "Mon",
    [DayOfWeek.Tuesday]: "Tue",
    [DayOfWeek.Wednesday]: "Wed",
    [DayOfWeek.Thursday]: "Thu",
    [DayOfWeek.Friday]: "Fri",
    [DayOfWeek.Saturday]: "Sat",
  };

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
        return "g";
      case "site_age":
        return "h";
      case "sensor_age":
        return "d";
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
<div class="flex flex-wrap items-center gap-1.5">
  {#if node.type === "threshold" && node.threshold}
    <Select.Root
      type="single"
      value={node.threshold.direction ?? "below"}
      onValueChange={(v) => {
        if (node.threshold) node.threshold.direction = v as "above" | "below";
      }}
    >
      <Select.Trigger class="h-7 w-[5rem] px-2 text-xs">
        {node.threshold.direction === "above" ? ">" : "<"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="below" label="<" />
        <Select.Item value="above" label=">" />
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      class="h-7 w-20 px-2 text-right text-xs tabular-nums"
      value={node.threshold.value ?? 0}
      oninput={(e) => {
        if (node.threshold)
          node.threshold.value = parseNumber(
            e.currentTarget.value,
            node.threshold.value ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">mg/dL</span>
  {:else if node.type === "predicted" && node.predicted}
    <Select.Root
      type="single"
      value={node.predicted.operator ?? "<="}
      onValueChange={(v) => {
        if (node.predicted) node.predicted.operator = v as ComparisonOperator;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {opLabels[(node.predicted.operator as ComparisonOperator) ?? "<="]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(opLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      class="h-7 w-20 px-2 text-right text-xs tabular-nums"
      value={node.predicted.value ?? 0}
      oninput={(e) => {
        if (node.predicted)
          node.predicted.value = parseNumber(
            e.currentTarget.value,
            node.predicted.value ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">mg/dL within</span>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      value={node.predicted.within_minutes ?? 0}
      oninput={(e) => {
        if (node.predicted)
          node.predicted.within_minutes = parseNumber(
            e.currentTarget.value,
            node.predicted.within_minutes ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "rate_of_change" && node.rate_of_change}
    <Select.Root
      type="single"
      value={node.rate_of_change.direction ?? "falling"}
      onValueChange={(v) => {
        if (node.rate_of_change)
          node.rate_of_change.direction = v as "rising" | "falling";
      }}
    >
      <Select.Trigger class="h-7 w-24 px-2 text-xs">
        {node.rate_of_change.direction ?? "falling"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="falling" label="falling" />
        <Select.Item value="rising" label="rising" />
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">≥</span>
    <Input
      type="number"
      step="0.1"
      class="h-7 w-20 px-2 text-right text-xs tabular-nums"
      value={node.rate_of_change.rate ?? 0}
      oninput={(e) => {
        if (node.rate_of_change)
          node.rate_of_change.rate = parseNumber(
            e.currentTarget.value,
            node.rate_of_change.rate ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">mg/dL/min</span>
  {:else if node.type === "trend" && node.trend}
    <Select.Root
      type="single"
      value={node.trend.bucket ?? "falling"}
      onValueChange={(v) => {
        if (node.trend) node.trend.bucket = v as TrendBucket;
      }}
    >
      <Select.Trigger class="h-7 w-32 px-2 text-xs">
        {trendLabels[(node.trend.bucket as TrendBucket) ?? "falling"]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(trendLabels) as [bucket, label] (bucket)}
          <Select.Item value={bucket} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
  {:else if node.type === "staleness" && node.staleness}
    <Select.Root
      type="single"
      value={node.staleness.operator ?? ">="}
      onValueChange={(v) => {
        if (node.staleness) node.staleness.operator = v;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {opLabels[(node.staleness.operator as ComparisonOperator) ?? ">="]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(opLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      value={node.staleness.value ?? 0}
      oninput={(e) => {
        if (node.staleness)
          node.staleness.value = parseNumber(
            e.currentTarget.value,
            node.staleness.value ?? 0,
          );
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "time_of_day" && node.time_of_day}
    <Input
      type="time"
      class="h-7 w-24 px-2 text-xs tabular-nums"
      value={node.time_of_day.from ?? "00:00"}
      oninput={(e) => {
        if (node.time_of_day) node.time_of_day.from = e.currentTarget.value;
      }}
    />
    <span class="text-xs text-muted-foreground">–</span>
    <Input
      type="time"
      class="h-7 w-24 px-2 text-xs tabular-nums"
      value={node.time_of_day.to ?? "23:59"}
      oninput={(e) => {
        if (node.time_of_day) node.time_of_day.to = e.currentTarget.value;
      }}
    />
  {:else if (node.type === "iob" || node.type === "cob" || node.type === "reservoir" || node.type === "site_age" || node.type === "sensor_age" || node.type === "pump_battery" || node.type === "uploader_battery" || node.type === "sensitivity_ratio") && node[node.type]}
    {@const payload = node[node.type]!}
    {@const suffix = leafSuffix(node.type)}
    <Select.Root
      type="single"
      value={(payload.operator as ComparisonOperator) ?? ">="}
      onValueChange={(v) => {
        payload.operator = v as ComparisonOperator;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {opLabels[(payload.operator as ComparisonOperator) ?? ">="]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(opLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      step={node.type === "sensitivity_ratio" ? "0.01" : "0.1"}
      class="h-7 w-20 px-2 text-right text-xs tabular-nums"
      value={payload.value ?? 0}
      oninput={(e) => {
        payload.value = parseNumber(e.currentTarget.value, payload.value ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">{suffix}</span>
  {:else if (node.type === "loop_stale" || node.type === "loop_enaction_stale") && node[node.type]}
    {@const payload = node[node.type]!}
    <Select.Root
      type="single"
      value={(payload.operator as StalenessOperator) ?? ">"}
      onValueChange={(v) => {
        payload.operator = v as StalenessOperator;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {stalenessOpLabels[(payload.operator as StalenessOperator) ?? ">"]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(stalenessOpLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      value={payload.minutes ?? 0}
      oninput={(e) => {
        payload.minutes = parseNumber(e.currentTarget.value, payload.minutes ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "signal_loss" && node.signal_loss}
    <span class="text-xs text-muted-foreground">no data for ≥</span>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      value={node.signal_loss.timeout_minutes ?? 0}
      oninput={(e) => {
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
      value={(node.temp_basal.metric ?? TempBasalMetric.Rate) as string}
      onValueChange={(v) => {
        if (node.temp_basal) node.temp_basal.metric = v as TempBasalMetric;
      }}
    >
      <Select.Trigger class="h-7 w-32 px-2 text-xs">
        {tempBasalMetricLabels[
          (node.temp_basal.metric as TempBasalMetric) ?? TempBasalMetric.Rate
        ]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(tempBasalMetricLabels) as [m, label] (m)}
          <Select.Item value={m} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Select.Root
      type="single"
      value={(node.temp_basal.operator as ComparisonOperator) ?? ">="}
      onValueChange={(v) => {
        if (node.temp_basal) node.temp_basal.operator = v as ComparisonOperator;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {opLabels[(node.temp_basal.operator as ComparisonOperator) ?? ">="]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(opLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      step="0.1"
      class="h-7 w-20 px-2 text-right text-xs tabular-nums"
      value={node.temp_basal.value ?? 0}
      oninput={(e) => {
        if (node.temp_basal)
          node.temp_basal.value = parseNumber(
            e.currentTarget.value,
            node.temp_basal.value ?? 0,
          );
      }}
    />
  {:else if (node.type === "pump_suspended" || node.type === "override_active" || node.type === "do_not_disturb") && node[node.type]}
    {@const payload = node[node.type]!}
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      checked={payload.is_active ?? true}
      onCheckedChange={(checked) => {
        payload.is_active = checked;
        if (!checked) payload.for_minutes = null;
      }}
    />
    <span class="text-xs text-muted-foreground">{payload.is_active ? "active" : "inactive"}</span>
    {#if payload.is_active}
      <span class="text-xs text-muted-foreground">for ≥</span>
      <Input
        type="number"
        min="1"
        class="h-7 w-16 px-2 text-right text-xs tabular-nums"
        placeholder="any"
        value={payload.for_minutes ?? ""}
        oninput={(e) => {
          const v = e.currentTarget.value;
          payload.for_minutes = v.length > 0 ? parseNumber(v, 0) : null;
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
      <Select.Trigger class="h-7 w-44 px-2 text-xs">
        {selectedRule?.name ?? "Select a rule"}
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
      <Select.Trigger class="h-7 w-32 px-2 text-xs">
        {node.alert_state.state ?? "firing"}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="firing" label="firing" />
        <Select.Item value="unacknowledged" label="unacknowledged" />
        <Select.Item value="acknowledged" label="acknowledged" />
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">for ≥</span>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      placeholder="any"
      value={node.alert_state.for_minutes ?? ""}
      oninput={(e) => {
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
      value={[...selected] as string[]}
      onValueChange={(v) => {
        if (node.glucose_bucket)
          node.glucose_bucket.buckets = v as GlucoseBucket[];
      }}
      class="flex flex-wrap gap-1"
    >
      {#each Object.entries(glucoseBucketLabels) as [bucket, label] (bucket)}
        <ToggleGroup.Item value={bucket} aria-label={label} class="h-7 px-2 text-xs">
          {label}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  {:else if (node.type === "time_since_last_carb" || node.type === "time_since_last_bolus") && node[node.type]}
    {@const payload = node[node.type]! as TimeSinceLastCarbCondition | TimeSinceLastBolusCondition}
    <Select.Root
      type="single"
      value={(payload.operator as unknown as ComparisonOperator) ?? ">="}
      onValueChange={(v) => {
        // AlertComparisonOperator is generated as a numeric enum but the wire
        // shape carries the symbol literal — cast at the boundary.
        payload.operator = v as unknown as typeof payload.operator;
      }}
    >
      <Select.Trigger class="h-7 w-14 px-2 text-xs">
        {opLabels[(payload.operator as unknown as ComparisonOperator) ?? ">="]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(opLabels) as [op, label] (op)}
          <Select.Item value={op} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="number"
      min="1"
      class="h-7 w-16 px-2 text-right text-xs tabular-nums"
      value={payload.minutes ?? 0}
      oninput={(e) => {
        payload.minutes = parseNumber(e.currentTarget.value, payload.minutes ?? 0);
      }}
    />
    <span class="text-xs text-muted-foreground">min</span>
  {:else if node.type === "day_of_week" && node.day_of_week}
    {@const selectedDays = new Set(node.day_of_week.days ?? [])}
    <ToggleGroup.Root
      type="multiple"
      size="sm"
      value={[...selectedDays].map(String)}
      onValueChange={(v) => {
        if (node.day_of_week)
          node.day_of_week.days = v.map((s) => Number(s) as DayOfWeek);
      }}
      class="flex flex-wrap gap-1"
    >
      {#each weekdayOrder as day (day)}
        <ToggleGroup.Item value={String(day)} aria-label={weekdayLabels[day]} class="h-7 w-9 px-1 text-xs">
          {weekdayLabels[day]}
        </ToggleGroup.Item>
      {/each}
    </ToggleGroup.Root>
  {:else if node.type === "pump_state" && node.pump_state}
    <Select.Root
      type="single"
      value={(node.pump_state.mode ?? PumpModeState.Suspended) as string}
      onValueChange={(v) => {
        if (node.pump_state) node.pump_state.mode = v as PumpModeState;
      }}
    >
      <Select.Trigger class="h-7 w-32 px-2 text-xs">
        {pumpModeLabels[(node.pump_state.mode as PumpModeState) ?? PumpModeState.Suspended]}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(pumpModeLabels) as [mode, label] (mode)}
          <Select.Item value={mode} {label} />
        {/each}
      </Select.Content>
    </Select.Root>
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      checked={node.pump_state.is_active ?? true}
      onCheckedChange={(checked) => {
        if (!node.pump_state) return;
        node.pump_state.is_active = checked;
        if (!checked) node.pump_state.for_minutes = undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">{node.pump_state.is_active ? "active" : "inactive"}</span>
    {#if node.pump_state.is_active}
      <span class="text-xs text-muted-foreground">for ≥</span>
      <Input
        type="number"
        min="1"
        class="h-7 w-16 px-2 text-right text-xs tabular-nums"
        placeholder="any"
        value={node.pump_state.for_minutes ?? ""}
        oninput={(e) => {
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
      value={(node.state_span_active.category ?? StateSpanCategory.Override) as string}
      onValueChange={(v) => {
        if (node.state_span_active)
          node.state_span_active.category = v as StateSpanCategory;
      }}
    >
      <Select.Trigger class="h-7 w-40 px-2 text-xs">
        {stateCategoryLabels[
          (node.state_span_active.category as StateSpanCategory) ?? StateSpanCategory.Override
        ] ?? "category"}
      </Select.Trigger>
      <Select.Content>
        {#each Object.entries(stateCategoryLabels) as [cat, label] (cat)}
          <Select.Item value={cat} label={label ?? cat} />
        {/each}
      </Select.Content>
    </Select.Root>
    <Input
      type="text"
      class="h-7 w-28 px-2 text-xs"
      placeholder="any state"
      value={node.state_span_active.state ?? ""}
      oninput={(e) => {
        if (!node.state_span_active) return;
        const v = e.currentTarget.value;
        node.state_span_active.state = v.length > 0 ? v : undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">is</span>
    <Switch
      checked={node.state_span_active.is_active ?? true}
      onCheckedChange={(checked) => {
        if (!node.state_span_active) return;
        node.state_span_active.is_active = checked;
        if (!checked) node.state_span_active.for_minutes = undefined;
      }}
    />
    <span class="text-xs text-muted-foreground">{node.state_span_active.is_active ? "active" : "inactive"}</span>
    {#if node.state_span_active.is_active}
      <span class="text-xs text-muted-foreground">for ≥</span>
      <Input
        type="number"
        min="1"
        class="h-7 w-16 px-2 text-right text-xs tabular-nums"
        placeholder="any"
        value={node.state_span_active.for_minutes ?? ""}
        oninput={(e) => {
          if (!node.state_span_active) return;
          const v = e.currentTarget.value;
          node.state_span_active.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
        }}
      />
      <span class="text-xs text-muted-foreground">min</span>
    {/if}
  {/if}
</div>

