<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { SlidersHorizontal } from "lucide-svelte";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import ColorFocusRange from "./ColorFocusRange.svelte";
  import type { GlucoseColorThresholds as GlucoseThresholds } from "$lib/utils/metric-color-focus";

  type HeatmapMetric =
    | "avgGlucose"
    | "tir"
    | "bolus"
    | "basal"
    | "tdd"
    | "carbs";

  const COLOR_PALETTES = [
    { label: "Theme", low: undefined, high: undefined },
    { label: "Blue / Orange", low: "#0072b2", high: "#e69f00" },
    { label: "Viridis", low: "#440154", high: "#fde725" },
    { label: "Cividis", low: "#00204c", high: "#ffea46" },
    { label: "Blue → Green → Yellow → Red", low: "#2563eb", high: "#dc2626" },
  ];

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
    focusBand = null,
    onFocusBandChange = () => {},
    lowColor = undefined,
    highColor = undefined,
    advancedMode = false,
    onAdvancedModeChange = () => {},
    transparencyPercent = 90,
    onTransparencyChange = () => {},
    onCustomColorsChange = () => {},
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
    focusBand?: readonly [number, number] | null;
    onFocusBandChange?: (value: [number, number] | null) => void;
    lowColor?: string;
    highColor?: string;
    advancedMode?: boolean;
    onAdvancedModeChange?: (val: boolean) => void;
    transparencyPercent?: number;
    onTransparencyChange?: (val: number | undefined) => void;
    onCustomColorsChange?: (low: string | undefined, high: string | undefined) => void;
  }>();

  let isPoppedOut = $state(false);

  const currentMetricOption = $derived(
    METRIC_OPTIONS.find((o) => o.value === selectedMetric)
  );
  const currentMetricLabel = $derived(currentMetricOption?.label ?? "Scale");
</script>

<!-- Scale panel: inline by default, optional floating top-right mode. -->
<div class="{isPoppedOut ? 'fixed top-20 right-6 z-40 shadow-2xl max-h-[calc(100vh-6rem)] overflow-y-auto' : 'relative mb-6'} print:static print:max-w-none print:shadow-none rounded-xl border border-border/80 bg-card/95 backdrop-blur-md p-3.5 space-y-3 w-[460px] max-w-[calc(100vw-2.5rem)] transition-all">
    <!-- Top bar: Metric Selector, Advanced Checkbox & Minimize Button -->
    <div class="flex items-center justify-between gap-2 border-b border-border/40 pb-2.5">
      <div class="flex items-center gap-2 min-w-0">
        <Select.Root
          type="single"
          value={selectedMetric}
          onValueChange={(v) => {
            if (v) selectedMetric = v as HeatmapMetric;
          }}
        >
          <Select.Trigger class="w-[145px] h-8 text-xs print:hidden">
            <span class="truncate">
              {currentMetricLabel}
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

        <!-- Advanced Settings Checkbox -->
        <label class="flex items-center gap-1.5 font-medium cursor-pointer text-foreground/90 text-xs shrink-0 select-none">
          <input
            type="checkbox"
            checked={advancedMode}
            onchange={(e) => onAdvancedModeChange(e.currentTarget.checked)}
            class="h-3.5 w-3.5 rounded border-border text-primary focus:ring-primary/40 cursor-pointer"
          />
          Advanced
        </label>
      </div>

      <!-- Dock / undock Button -->
      <Button
        variant="ghost"
        size="sm"
        class="h-7 px-2 shrink-0 text-muted-foreground hover:text-foreground hover:bg-muted/80 rounded-md gap-1.5 text-xs"
        title={isPoppedOut ? "Return panel to the page" : "Float panel in the top-right corner"}
        onclick={() => isPoppedOut = !isPoppedOut}
      >
        <SlidersHorizontal class="h-3.5 w-3.5" />
        {isPoppedOut ? "Dock" : "Float"}
      </Button>
    </div>

    <!-- Sliders & Ranges (Always fixed compact width) -->
    {#if selectedMetric === "avgGlucose"}
      <div class="w-full">
        {#if advancedMode}
          <ColorFocusRange
            metricKey="avgGlucose"
            glucose
            {units}
            thresholds={glucoseThresholds}
            stops={HEATMAP_STOPS}
            onThresholdsChange={onGlucoseThresholdsChange}
            {focusBand}
            onFocusBandChange={onFocusBandChange}
            {transparencyPercent}
            {onTransparencyChange}
          />
        {:else}
          <!-- Default simple scale preview -->
          <div class="w-full text-xs text-muted-foreground space-y-1.5">
            <span
              class="block h-3.5 w-full rounded-sm"
              style:background="linear-gradient(to right in srgb, {HEATMAP_STOPS.map((s) => `${s.color} ${((s.mgdl - 40) / (350 - 40)) * 100}%`).join(', ')})"
            ></span>
            <div class="flex justify-between text-[11px] tabular-nums">
              <span>40 {units === "mmol" ? "mmol/L" : "mg/dL"}</span>
              <span class="text-muted-foreground">Default scale</span>
              <span>350 {units === "mmol" ? "mmol/L" : "mg/dL"}</span>
            </div>
          </div>
        {/if}
      </div>
    {:else}
      {@const metricLabel = currentMetricLabel}
      {@const metricUnit =
        selectedMetric === "tir" ? "%" : selectedMetric === "carbs" ? "g" : "U"}
      {@const metricMax =
        selectedMetric === "tir" ? 100 : getMetricMax(selectedMetric)}
      {@const cssVar =
        METRIC_CSS_VARS[selectedMetric as Exclude<HeatmapMetric, "avgGlucose">]}
      <div class="w-full">
        {#if advancedMode}
          {#key `${selectedMetric}-${lowColor}-${highColor}`}
            <ColorFocusRange
              metricKey={selectedMetric}
              {metricLabel}
              unit={metricUnit}
              observedMax={metricMax}
              {cssVar}
              fixedMax={selectedMetric === "tir" ? 100 : undefined}
              {focusRange}
              {onFocusRangeChange}
              {focusBand}
              {onFocusBandChange}
              {lowColor}
              {highColor}
              {COLOR_PALETTES}
              {onCustomColorsChange}
              {transparencyPercent}
              {onTransparencyChange}
            />
          {/key}
        {:else}
          <!-- Default simple scale preview -->
          <div class="w-full text-xs text-muted-foreground space-y-1.5">
            <span
              class="block h-3.5 w-full rounded-sm"
              style:background="linear-gradient(to right, color-mix(in srgb, var({cssVar}) 15%, transparent) 0%, var({cssVar}) 100%)"
            ></span>
            <div class="flex justify-between text-[11px] tabular-nums">
              <span>0 {metricUnit}</span>
              <span class="text-muted-foreground">Default {metricLabel.toLowerCase()} scale</span>
              <span>{metricMax} {metricUnit}</span>
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </div>
