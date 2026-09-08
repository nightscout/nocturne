<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import ColorFocusRange from "./ColorFocusRange.svelte";
  import GlucoseColorThresholds from "./GlucoseColorThresholds.svelte";
  import type { GlucoseColorThresholds as GlucoseThresholds } from "$lib/utils/metric-color-focus";

  type HeatmapMetric =
    | "avgGlucose"
    | "tir"
    | "bolus"
    | "basal"
    | "tdd"
    | "carbs";

  let {
    selectedMetric = $bindable("avgGlucose"),
    units,
    METRIC_OPTIONS,
    HEATMAP_STOPS,
    METRIC_CSS_VARS,
    getMetricMax,
    focusRange = null,
    onFocusRangeChange = () => {},
    glucoseThresholds,
    onGlucoseThresholdsChange = () => {},
  } = $props<{
    selectedMetric: HeatmapMetric;
    units: GlucoseUnits;
    METRIC_OPTIONS: { value: HeatmapMetric; label: string }[];
    HEATMAP_STOPS: ReadonlyArray<{ mgdl: number; color: string }>;
    METRIC_CSS_VARS: Record<Exclude<HeatmapMetric, "avgGlucose">, string>;
    getMetricMax: (metric: HeatmapMetric) => number;
    focusRange?: readonly [number, number] | null;
    onFocusRangeChange?: (range: [number, number] | null) => void;
    glucoseThresholds: GlucoseThresholds;
    onGlucoseThresholdsChange?: (value: GlucoseThresholds | null) => void;
  }>();
</script>

<div class="mb-6 rounded-lg border border-border bg-card p-3">
  {#if selectedMetric === "avgGlucose"}
    <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
      <Select.Root
        type="single"
        value={selectedMetric}
        onValueChange={(v) => {
          if (v) selectedMetric = v as HeatmapMetric;
        }}
      >
        <Select.Trigger class="w-[150px] h-8 text-xs print:hidden">
          <span class="truncate">
            {METRIC_OPTIONS.find(
              (o: { value: HeatmapMetric; label: string }) =>
                o.value === selectedMetric
            )?.label ?? "Avg Glucose"}
          </span>
        </Select.Trigger>
        <Select.Content>
          {#each METRIC_OPTIONS as option}
            <Select.Item value={option.value}>
              {option.label}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
      <GlucoseColorThresholds
        {units}
        thresholds={glucoseThresholds}
        stops={HEATMAP_STOPS}
        onThresholdsChange={onGlucoseThresholdsChange}
      />
      <div class="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span
          class="inline-block h-3 w-3 rounded-sm"
          style="background: var(--muted)"
        ></span>
        Other Data (no glucose)
      </div>
    </div>
  {:else}
    {@const metricLabel =
      METRIC_OPTIONS.find(
        (o: { value: HeatmapMetric; label: string }) =>
          o.value === selectedMetric
      )?.label ?? ""}
    {@const metricUnit =
      selectedMetric === "tir" ? "%" : selectedMetric === "carbs" ? "g" : "U"}
    {@const metricMax =
      selectedMetric === "tir" ? 100 : getMetricMax(selectedMetric)}
    {@const cssVar =
      METRIC_CSS_VARS[selectedMetric as Exclude<HeatmapMetric, "avgGlucose">]}
    <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
      <Select.Root
        type="single"
        value={selectedMetric}
        onValueChange={(v) => {
          if (v) selectedMetric = v as HeatmapMetric;
        }}
      >
        <Select.Trigger class="w-[150px] h-8 text-xs print:hidden">
          <span class="truncate">
            {METRIC_OPTIONS.find(
              (o: { value: HeatmapMetric; label: string }) =>
                o.value === selectedMetric
            )?.label ?? "Avg Glucose"}
          </span>
        </Select.Trigger>
        <Select.Content>
          {#each METRIC_OPTIONS as option}
            <Select.Item value={option.value}>
              {option.label}
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
      <ColorFocusRange
        {metricLabel}
        unit={metricUnit}
        observedMax={metricMax}
        {cssVar}
        fixedMax={selectedMetric === "tir" ? 100 : undefined}
        {focusRange}
        {onFocusRangeChange}
      />
      <div class="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span
          class="inline-block h-3 w-3 rounded-sm"
          style="background: var(--muted)"
        ></span>
        No {metricLabel.toLowerCase()} data
      </div>
    </div>
  {/if}
</div>
