<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { Switch } from "$lib/components/ui/switch";
  import { Plus, X } from "lucide-svelte";
  import Self from "./RuleBuilder.svelte";
  import {
    defaultPayload,
    type ConditionKind,
    type ConditionNode,
    type ComparisonOperator,
    type TrendBucket,
    type TempBasalMetric,
  } from "./types";

  interface AvailableRule {
    id: string;
    name: string;
  }

  interface Props {
    node: ConditionNode;
    availableRules?: AvailableRule[];
    onRemove?: () => void;
  }

  let {
    node = $bindable(),
    availableRules = [],
    onRemove,
  }: Props = $props();

  const kindLabels: Record<ConditionKind, string> = {
    composite: "Group (and/or)",
    not: "Not",
    sustained: "Sustained for...",
    threshold: "Threshold (mg/dL)",
    rate_of_change: "Rate of change",
    staleness: "Data staleness",
    predicted: "Predicted glucose",
    trend: "Trend bucket",
    time_of_day: "Time of day",
    iob: "Insulin on board",
    cob: "Carbs on board",
    reservoir: "Reservoir level",
    site_age: "Site age",
    sensor_age: "Sensor age",
    alert_state: "Other rule state",
    loop_stale: "Loop has stopped",
    loop_enaction_stale: "Loop not enacting",
    pump_suspended: "Pump suspended",
    pump_battery: "Pump battery",
    temp_basal: "Temp basal",
    uploader_battery: "Phone battery",
    override_active: "Override active",
    sensitivity_ratio: "Insulin sensitivity",
  };

  const kinds: ConditionKind[] = [
    "composite",
    "not",
    "sustained",
    "threshold",
    "rate_of_change",
    "staleness",
    "predicted",
    "trend",
    "time_of_day",
    "iob",
    "cob",
    "reservoir",
    "site_age",
    "sensor_age",
    "alert_state",
    "loop_stale",
    "loop_enaction_stale",
    "pump_suspended",
    "pump_battery",
    "temp_basal",
    "uploader_battery",
    "override_active",
    "sensitivity_ratio",
  ];

  const tempBasalMetricLabels: Record<TempBasalMetric, string> = {
    rate: "Rate (U/hr)",
    percent_of_scheduled: "Percent of scheduled",
  };

  const operatorLabels: Record<ComparisonOperator, string> = {
    ">=": "≥",
    ">": ">",
    "<=": "≤",
    "<": "<",
  };

  const trendLabels: Record<TrendBucket, string> = {
    falling_fast: "Falling fast",
    falling: "Falling",
    flat: "Flat",
    rising: "Rising",
    rising_fast: "Rising fast",
  };

  function changeKind(next: ConditionKind) {
    if (next === node.type) return;
    node = defaultPayload(next);
  }

  function ensurePayload<K extends ConditionKind>(kind: K): NonNullable<ConditionNode[K]> {
    const existing = node[kind];
    if (existing) return existing as NonNullable<ConditionNode[K]>;
    const fresh = defaultPayload(kind)[kind];
    node[kind] = fresh as ConditionNode[K];
    return fresh as NonNullable<ConditionNode[K]>;
  }

  function addCompositeChild() {
    const payload = ensurePayload("composite");
    payload.conditions = [...payload.conditions, defaultPayload("threshold")];
  }

  function removeCompositeChild(index: number) {
    const payload = ensurePayload("composite");
    payload.conditions = payload.conditions.filter((_, i) => i !== index);
  }

  function parseNumber(value: string, fallback: number): number {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }
</script>

<div class="rounded-md border bg-background p-3 space-y-3">
  <div class="flex items-center gap-2">
    <div class="flex-1">
      <Select.Root
        type="single"
        value={node.type}
        onValueChange={(v) => changeKind(v as ConditionKind)}
      >
        <Select.Trigger>{kindLabels[node.type]}</Select.Trigger>
        <Select.Content>
          {#each kinds as kind (kind)}
            <Select.Item value={kind} label={kindLabels[kind]} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    {#if onRemove}
      <Button
        variant="ghost"
        size="icon"
        onclick={onRemove}
        aria-label="Remove condition"
      >
        <X class="h-4 w-4" />
      </Button>
    {/if}
  </div>

  {#if node.type === "composite"}
    {@const payload = ensurePayload("composite")}
    <div class="space-y-3">
      <div class="space-y-2">
        <Label>Match</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as "and" | "or";
          }}
        >
          <Select.Trigger>
            {payload.operator === "and" ? "All conditions (AND)" : "Any condition (OR)"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="and" label="All conditions (AND)" />
            <Select.Item value="or" label="Any condition (OR)" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2 pl-3 border-l">
        {#each payload.conditions as child, i (child._uid)}
          <Self
            bind:node={payload.conditions[i]}
            {availableRules}
            onRemove={() => removeCompositeChild(i)}
          />
        {/each}
        <Button variant="outline" size="sm" onclick={addCompositeChild}>
          <Plus class="h-4 w-4 mr-2" />
          Add condition
        </Button>
      </div>
    </div>
  {:else if node.type === "not"}
    {@const payload = ensurePayload("not")}
    <div class="pl-3 border-l">
      <Self bind:node={payload.child} {availableRules} />
    </div>
  {:else if node.type === "sustained"}
    {@const payload = ensurePayload("sustained")}
    <div class="space-y-2">
      <Label for="sustained-minutes">Minutes</Label>
      <Input
        id="sustained-minutes"
        type="number"
        min="1"
        value={payload.minutes}
        oninput={(e) => {
          payload.minutes = parseNumber(e.currentTarget.value, payload.minutes);
        }}
      />
    </div>
    <div class="pl-3 border-l">
      <Self bind:node={payload.child} {availableRules} />
    </div>
  {:else if node.type === "threshold"}
    {@const payload = ensurePayload("threshold")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Direction</Label>
        <Select.Root
          type="single"
          value={payload.direction}
          onValueChange={(v) => {
            payload.direction = v as "above" | "below";
          }}
        >
          <Select.Trigger>{payload.direction === "above" ? "Above" : "Below"}</Select.Trigger>
          <Select.Content>
            <Select.Item value="below" label="Below" />
            <Select.Item value="above" label="Above" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="threshold-value">Value (mg/dL)</Label>
        <Input
          id="threshold-value"
          type="number"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "rate_of_change"}
    {@const payload = ensurePayload("rate_of_change")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Direction</Label>
        <Select.Root
          type="single"
          value={payload.direction}
          onValueChange={(v) => {
            payload.direction = v as "rising" | "falling";
          }}
        >
          <Select.Trigger>{payload.direction === "rising" ? "Rising" : "Falling"}</Select.Trigger>
          <Select.Content>
            <Select.Item value="falling" label="Falling" />
            <Select.Item value="rising" label="Rising" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="roc-rate">Rate (mg/dL per min)</Label>
        <Input
          id="roc-rate"
          type="number"
          step="0.1"
          value={payload.rate}
          oninput={(e) => {
            payload.rate = parseNumber(e.currentTarget.value, payload.rate);
          }}
        />
      </div>
    </div>
  {:else if node.type === "staleness"}
    {@const payload = ensurePayload("staleness")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="staleness-value">Minutes</Label>
        <Input
          id="staleness-value"
          type="number"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "predicted"}
    {@const payload = ensurePayload("predicted")}
    <div class="grid grid-cols-3 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="predicted-value">Value (mg/dL)</Label>
        <Input
          id="predicted-value"
          type="number"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
      <div class="space-y-2">
        <Label for="predicted-within">Within (min)</Label>
        <Input
          id="predicted-within"
          type="number"
          value={payload.within_minutes}
          oninput={(e) => {
            payload.within_minutes = parseNumber(
              e.currentTarget.value,
              payload.within_minutes,
            );
          }}
        />
      </div>
    </div>
  {:else if node.type === "trend"}
    {@const payload = ensurePayload("trend")}
    <div class="space-y-2">
      <Label>Bucket</Label>
      <Select.Root
        type="single"
        value={payload.bucket}
        onValueChange={(v) => {
          payload.bucket = v as TrendBucket;
        }}
      >
        <Select.Trigger>{trendLabels[payload.bucket]}</Select.Trigger>
        <Select.Content>
          {#each Object.entries(trendLabels) as [bucket, label] (bucket)}
            <Select.Item value={bucket} {label} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  {:else if node.type === "time_of_day"}
    {@const payload = ensurePayload("time_of_day")}
    <div class="grid grid-cols-3 gap-2">
      <div class="space-y-2">
        <Label for="tod-from">From</Label>
        <Input
          id="tod-from"
          type="time"
          value={payload.from}
          oninput={(e) => {
            payload.from = e.currentTarget.value;
          }}
        />
      </div>
      <div class="space-y-2">
        <Label for="tod-to">To</Label>
        <Input
          id="tod-to"
          type="time"
          value={payload.to}
          oninput={(e) => {
            payload.to = e.currentTarget.value;
          }}
        />
      </div>
      <div class="space-y-2">
        <Label for="tod-tz">Timezone (optional)</Label>
        <Input
          id="tod-tz"
          type="text"
          placeholder="UTC"
          value={payload.timezone ?? ""}
          oninput={(e) => {
            const v = e.currentTarget.value;
            payload.timezone = v.length > 0 ? v : undefined;
          }}
        />
      </div>
    </div>
  {:else if node.type === "iob" || node.type === "cob" || node.type === "reservoir" || node.type === "site_age" || node.type === "sensor_age"}
    {@const payload = node[node.type]!}
    {@const valueLabel =
      node.type === "iob"
        ? "Units"
        : node.type === "cob"
          ? "Grams"
          : node.type === "reservoir"
            ? "Units"
            : node.type === "site_age"
              ? "Hours"
              : "Days"}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="metric-value">{valueLabel}</Label>
        <Input
          id="metric-value"
          type="number"
          step="0.1"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "alert_state"}
    {@const payload = ensurePayload("alert_state")}
    {@const selectedRule = availableRules.find((r) => r.id === payload.alert_id)}
    <div class="space-y-2">
      <Label>Other rule</Label>
      <Select.Root
        type="single"
        value={payload.alert_id}
        onValueChange={(v) => {
          payload.alert_id = v;
        }}
      >
        <Select.Trigger>
          {selectedRule?.name ?? "Select a rule"}
        </Select.Trigger>
        <Select.Content>
          {#each availableRules as rule (rule.id)}
            <Select.Item value={rule.id} label={rule.name} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>State</Label>
        <Select.Root
          type="single"
          value={payload.state}
          onValueChange={(v) => {
            payload.state = v as "firing" | "acknowledged";
          }}
        >
          <Select.Trigger>
            {payload.state === "firing" ? "Firing" : "Acknowledged"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="firing" label="Firing" />
            <Select.Item value="acknowledged" label="Acknowledged" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="alert-state-for">For at least (min, optional)</Label>
        <Input
          id="alert-state-for"
          type="number"
          value={payload.for_minutes ?? ""}
          oninput={(e) => {
            const v = e.currentTarget.value;
            payload.for_minutes = v.length > 0 ? parseNumber(v, 0) : undefined;
          }}
        />
      </div>
    </div>
  {:else if node.type === "loop_stale"}
    {@const payload = ensurePayload("loop_stale")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            <Select.Item value=">" label=">" />
            <Select.Item value=">=" label="≥" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="loop-stale-minutes">Minutes</Label>
        <Input
          id="loop-stale-minutes"
          type="number"
          min="1"
          value={payload.minutes}
          oninput={(e) => {
            payload.minutes = parseNumber(e.currentTarget.value, payload.minutes);
          }}
        />
      </div>
    </div>
  {:else if node.type === "loop_enaction_stale"}
    {@const payload = ensurePayload("loop_enaction_stale")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            <Select.Item value=">" label=">" />
            <Select.Item value=">=" label="≥" />
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="loop-enaction-stale-minutes">Minutes</Label>
        <Input
          id="loop-enaction-stale-minutes"
          type="number"
          min="1"
          value={payload.minutes}
          oninput={(e) => {
            payload.minutes = parseNumber(e.currentTarget.value, payload.minutes);
          }}
        />
      </div>
    </div>
    <p class="text-xs text-muted-foreground">
      For closed-loop users only. Open-loop users should use "Loop has stopped" instead.
    </p>
  {:else if node.type === "pump_suspended"}
    {@const payload = ensurePayload("pump_suspended")}
    <div class="space-y-3">
      <div class="flex items-center justify-between gap-2">
        <Label for="pump-suspended-active">Pump is currently suspended</Label>
        <Switch
          id="pump-suspended-active"
          checked={payload.is_active}
          onCheckedChange={(checked) => {
            payload.is_active = checked;
            if (!checked) payload.for_minutes = null;
          }}
        />
      </div>
      {#if payload.is_active}
        <div class="space-y-2">
          <Label for="pump-suspended-for">For at least (min, optional)</Label>
          <Input
            id="pump-suspended-for"
            type="number"
            min="1"
            value={payload.for_minutes ?? ""}
            oninput={(e) => {
              const v = e.currentTarget.value;
              payload.for_minutes = v.length > 0 ? parseNumber(v, 0) : null;
            }}
          />
        </div>
      {/if}
    </div>
  {:else if node.type === "pump_battery"}
    {@const payload = ensurePayload("pump_battery")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="pump-battery-value">Percent</Label>
        <Input
          id="pump-battery-value"
          type="number"
          min="0"
          max="100"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "temp_basal"}
    {@const payload = ensurePayload("temp_basal")}
    {@const valueLabel = payload.metric === "rate" ? "U/hr" : "%"}
    <div class="space-y-2">
      <Label>Metric</Label>
      <Select.Root
        type="single"
        value={payload.metric}
        onValueChange={(v) => {
          payload.metric = v as TempBasalMetric;
        }}
      >
        <Select.Trigger>{tempBasalMetricLabels[payload.metric]}</Select.Trigger>
        <Select.Content>
          {#each Object.entries(tempBasalMetricLabels) as [m, label] (m)}
            <Select.Item value={m} {label} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="temp-basal-value">{valueLabel}</Label>
        <Input
          id="temp-basal-value"
          type="number"
          step="0.1"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "uploader_battery"}
    {@const payload = ensurePayload("uploader_battery")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="uploader-battery-value">Percent</Label>
        <Input
          id="uploader-battery-value"
          type="number"
          min="0"
          max="100"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
  {:else if node.type === "override_active"}
    {@const payload = ensurePayload("override_active")}
    <div class="space-y-3">
      <div class="flex items-center justify-between gap-2">
        <Label for="override-active-active">Override is currently active</Label>
        <Switch
          id="override-active-active"
          checked={payload.is_active}
          onCheckedChange={(checked) => {
            payload.is_active = checked;
            if (!checked) payload.for_minutes = null;
          }}
        />
      </div>
      {#if payload.is_active}
        <div class="space-y-2">
          <Label for="override-active-for">For at least (min, optional)</Label>
          <Input
            id="override-active-for"
            type="number"
            min="1"
            value={payload.for_minutes ?? ""}
            oninput={(e) => {
              const v = e.currentTarget.value;
              payload.for_minutes = v.length > 0 ? parseNumber(v, 0) : null;
            }}
          />
        </div>
      {/if}
    </div>
  {:else if node.type === "sensitivity_ratio"}
    {@const payload = ensurePayload("sensitivity_ratio")}
    <div class="grid grid-cols-2 gap-2">
      <div class="space-y-2">
        <Label>Operator</Label>
        <Select.Root
          type="single"
          value={payload.operator}
          onValueChange={(v) => {
            payload.operator = v as ComparisonOperator;
          }}
        >
          <Select.Trigger>{operatorLabels[payload.operator]}</Select.Trigger>
          <Select.Content>
            {#each Object.entries(operatorLabels) as [op, label] (op)}
              <Select.Item value={op} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      </div>
      <div class="space-y-2">
        <Label for="sensitivity-ratio-value">Ratio</Label>
        <Input
          id="sensitivity-ratio-value"
          type="number"
          step="0.01"
          value={payload.value}
          oninput={(e) => {
            payload.value = parseNumber(e.currentTarget.value, payload.value);
          }}
        />
      </div>
    </div>
    <p class="text-xs text-muted-foreground">
      Available for AAPS and Trio. Loop iOS does not report this value.
    </p>
  {/if}
</div>
