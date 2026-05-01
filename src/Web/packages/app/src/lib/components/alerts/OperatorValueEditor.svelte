<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import type { ComparisonOperator } from "./types";

  // Editor for the eight `{operator, value}` payload shapes (iob, cob,
  // reservoir, site_age, sensor_age, pump_battery, uploader_battery,
  // sensitivity_ratio) and the three `{operator, minutes}` shapes
  // (staleness, loop_stale, loop_enaction_stale). All eleven render the same
  // operator dropdown + numeric input pair; only the unit, step, bounds, and
  // operator subset differ. Kept as a separate component so RuleBuilder.svelte
  // doesn't have to repeat that markup eleven times.
  //
  // The component mutates `payload` in place rather than emitting events; the
  // surrounding `bind:node` chain in RuleBuilder propagates the change. This
  // matches the pattern the inline editors used and keeps the editor reactive
  // without a redundant onUpdate plumbing layer.

  // Loose payload shape so the staleness kinds (operator narrowed to
  // `>`/`>=`) and the {operator,value} kinds can both flow through. The
  // dropdown only emits values present in `operators`, so the wider
  // ComparisonOperator write below is safe at runtime.
  type AnyOpValuePayload = {
    operator: string;
    value?: number;
    minutes?: number;
  };

  interface Props {
    payload: AnyOpValuePayload;
    /** Which numeric field on `payload` this editor binds to. */
    field: "value" | "minutes";
    valueLabel: string;
    /** Step for the numeric input (default `1`). */
    step?: number | string;
    /** Optional min/max for the numeric input. */
    min?: number;
    max?: number;
    /** Subset of operators to show. Defaults to all four. */
    operators?: ComparisonOperator[];
    /** Stable id prefix for label/input pairing. */
    idPrefix: string;
  }

  let {
    payload = $bindable(),
    field,
    valueLabel,
    step = 1,
    min,
    max,
    operators = [">=", ">", "<=", "<"],
    idPrefix,
  }: Props = $props();

  const operatorLabels: Record<ComparisonOperator, string> = {
    ">=": "≥",
    ">": ">",
    "<=": "≤",
    "<": "<",
  };

  function parseNumber(value: string, fallback: number): number {
    const n = Number(value);
    return Number.isFinite(n) ? n : fallback;
  }
</script>

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
      <Select.Trigger>
        {operatorLabels[payload.operator as ComparisonOperator] ??
          payload.operator}
      </Select.Trigger>
      <Select.Content>
        {#each operators as op (op)}
          <Select.Item value={op} label={operatorLabels[op]} />
        {/each}
      </Select.Content>
    </Select.Root>
  </div>
  <div class="space-y-2">
    <Label for="{idPrefix}-value">{valueLabel}</Label>
    <Input
      id="{idPrefix}-value"
      type="number"
      {step}
      {min}
      {max}
      value={payload[field] ?? 0}
      oninput={(e) => {
        payload[field] = parseNumber(
          e.currentTarget.value,
          payload[field] ?? 0,
        );
      }}
    />
  </div>
</div>
