<script lang="ts" generics="Metric extends string">
  import * as Select from "$lib/components/ui/select";
  import Headroom from "$lib/components/layout/Headroom.svelte";
  import { formatGlucoseValue, getUnitLabel, type GlucoseUnits } from "$lib/utils/formatting";
  import ColorFocusRange from "./ColorFocusRange.svelte";
  import ChartKey from "$lib/components/charts/print/ChartKey.svelte";
  import {
    scaleGradient,
    type ColorBoundary,
    type ColorFocusRange as Range,
    type MetricScale,
  } from "$lib/utils/metric-color-focus";

  let {
    selectedMetric = $bindable(),
    metricOptions,
    units,
    unit,
    scale,
    limits,
    onWindowChange,
    onBoundariesChange,
    onDimChange,
    onReset,
    glucoseBands,
  }: {
    selectedMetric: Metric;
    metricOptions: { value: Metric; label: string }[];
    units: GlucoseUnits;
    /** The selected metric's unit; glucose is shown in `units` instead. */
    unit: string;
    scale: MetricScale;
    limits: Range;
    onWindowChange: (window: Range | null) => void;
    onBoundariesChange: (boundaries: ColorBoundary[] | null) => void;
    onDimChange: (dim: number) => void;
    onReset: () => void;
    /** Very-low, low, high and very-high boundaries in mg/dL, as the cells are hatched. */
    glucoseBands: readonly number[];
  } = $props();

  const glucose = $derived(selectedMetric === "avgGlucose");
  const metricLabel = $derived(
    metricOptions.find((o) => o.value === selectedMetric)?.label ?? "Scale"
  );
  const unitLabel = $derived(glucose ? getUnitLabel(units) : unit);
  const formatted = (value: number) =>
    glucose ? formatGlucoseValue(value, units) : String(Math.round(value * 1000) / 1000);

  const bandKey = $derived.by(() => {
    const [veryLow, low, high, veryHigh] = glucoseBands.map((mgdl) => formatGlucoseValue(mgdl, units));
    return [
      { texture: "very-low-hatch" as const, label: `Very low < ${veryLow} ${unitLabel}` },
      { texture: "low-hatch" as const, label: `Low < ${low}` },
      { texture: "high-hatch" as const, label: `High ≥ ${high}` },
      { texture: "very-high-hatch" as const, label: `Very high ≥ ${veryHigh}` },
    ];
  });
</script>

<Headroom class="mb-6 print:mb-4">
  <div
    class="color-focus rounded-xl border border-border/80 bg-card/95 p-3 shadow-sm backdrop-blur-md print:border-0 print:p-0 print:shadow-none"
  >
    <div class="print:hidden">
      <ColorFocusRange
        {metricLabel}
        {scale}
        {limits}
        {glucose}
        {units}
        {unit}
        {onWindowChange}
        {onBoundariesChange}
        {onDimChange}
        {onReset}
      >
        {#snippet leading()}
          <Select.Root
            type="single"
            value={selectedMetric}
            onValueChange={(v) => {
              const option = metricOptions.find((o) => o.value === v);
              if (option) selectedMetric = option.value;
            }}
          >
            <Select.Trigger size="sm" class="w-36">
              <span class="truncate">{metricLabel}</span>
            </Select.Trigger>
            <Select.Content>
              {#each metricOptions as option (option.value)}
                <Select.Item value={option.value}>{option.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        {/snippet}
      </ColorFocusRange>
    </div>

    <!-- The editor is all controls, so paper gets the scale it set. -->
    <div class="hidden space-y-1.5 text-xs text-muted-foreground print:block">
      <span class="text-sm font-medium text-foreground">{metricLabel}</span>
      <span
        class="block h-3.5 w-full rounded-sm bg-(image:--scale-gradient)"
        style:--scale-gradient={scaleGradient(scale)}
        role="img"
        aria-label={`${metricLabel} color scale`}
      ></span>
      <div class="flex justify-between tabular-nums">
        <span>{formatted(scale.axis[0])} {unitLabel}</span>
        <span>
          Focus {formatted(scale.window[0])}–{formatted(scale.window[1])} {unitLabel}
        </span>
        <span>{formatted(scale.axis[1])} {unitLabel}</span>
      </div>
      {#if glucose}
        <ChartKey items={bandKey} class="hidden justify-start [.chart-patterns-on_&]:flex" />
      {/if}
    </div>
  </div>
</Headroom>

<style>
  .color-focus {
    print-color-adjust: exact;
    -webkit-print-color-adjust: exact;
  }
</style>
