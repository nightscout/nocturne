<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { SlidersHorizontal } from "lucide-svelte";
  import { formatGlucoseValue, getUnitLabel, type GlucoseUnits } from "$lib/utils/formatting";
  import ColorFocusRange from "./ColorFocusRange.svelte";
  import {
    GLUCOSE_COLOR_MIN,
    GLUCOSE_COLOR_MAX,
    type GlucoseColorThresholds as GlucoseThresholds,
  } from "$lib/utils/metric-color-focus";

  type HeatmapMetric =
    | "avgGlucose"
    | "tir"
    | "bolus"
    | "basal"
    | "tdd"
    | "carbs";

  // Approximate stops for each colormap (https://sjmgarnier.github.io/viridis/articles/intro-to-viridis.html),
  // sampled at 0/25/50/75/100% so the picker previews and the actual scale both show the full spectrum.
  const COLOR_PALETTES = [
    { label: "Theme", colors: undefined },
    { label: "Viridis", colors: ["#440154", "#3b528b", "#21908d", "#5dc963", "#fde725"] },
    { label: "Plasma", colors: ["#0d0887", "#7e03a8", "#cc4778", "#f89441", "#f0f921"] },
    { label: "Inferno", colors: ["#000004", "#57106e", "#bc3754", "#f98c0a", "#fcffa4"] },
    { label: "Magma", colors: ["#000004", "#51127c", "#b73779", "#fc8961", "#fcfdbf"] },
    { label: "Cividis", colors: ["#00204d", "#414d6b", "#7c7b78", "#b6a069", "#ffe945"] },
    { label: "Turbo", colors: ["#30123b", "#4675ed", "#1ae4b6", "#a4fc3c", "#fb8022", "#7a0403"] },
    { label: "Mako", colors: ["#0b0405", "#35264c", "#2f6b8e", "#52c2ac", "#dbf6d7"] },
    { label: "Rocket", colors: ["#03051a", "#521635", "#a52c60", "#f2703a", "#fbeae3"] },
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
    metricColors = undefined,
    advancedMode = false,
    onAdvancedModeChange = () => {},
    transparencyPercent = 90,
    onTransparencyChange = () => {},
    onCustomColorsChange = () => {},
    invert = false,
    onInvertChange = () => {},
    themeStops = undefined,
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
    metricColors?: readonly string[];
    advancedMode?: boolean;
    onAdvancedModeChange?: (val: boolean) => void;
    transparencyPercent?: number;
    onTransparencyChange?: (val: number | undefined) => void;
    onCustomColorsChange?: (colors: string[] | undefined) => void;
    invert?: boolean;
    onInvertChange?: (value: boolean) => void;
    themeStops?: ReadonlyArray<{ mgdl: number; color: string }>;
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
          <Select.Trigger size="sm" class="w-[145px] print:hidden">
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
        variant="ghost-muted"
        size="xs"
        class="shrink-0"
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
          {#key `avgGlucose-${lowColor}-${highColor}-${invert}`}
            <ColorFocusRange
              metricKey="avgGlucose"
              glucose
              {units}
              thresholds={glucoseThresholds}
              stops={HEATMAP_STOPS}
              {themeStops}
              onThresholdsChange={onGlucoseThresholdsChange}
              {focusBand}
              onFocusBandChange={onFocusBandChange}
              {lowColor}
              {highColor}
              colors={metricColors}
              {COLOR_PALETTES}
              {onCustomColorsChange}
              {invert}
              {onInvertChange}
              {transparencyPercent}
              {onTransparencyChange}
            />
          {/key}
        {:else}
          <!-- Default simple scale preview -->
          <div class="w-full text-xs text-muted-foreground space-y-1.5">
            <span
              class="block h-3.5 w-full rounded-sm bg-(image:--heatmap-gradient)"
              style:--heatmap-gradient="linear-gradient(to right in srgb, {HEATMAP_STOPS.map((s) => `${s.color} ${((s.mgdl - GLUCOSE_COLOR_MIN) / (GLUCOSE_COLOR_MAX - GLUCOSE_COLOR_MIN)) * 100}%`).join(', ')})"
            ></span>
            <div class="flex justify-between text-[11px] tabular-nums">
              <span>{formatGlucoseValue(GLUCOSE_COLOR_MIN, units)} {getUnitLabel(units)}</span>
              <span class="text-muted-foreground">Default scale</span>
              <span>{formatGlucoseValue(GLUCOSE_COLOR_MAX, units)} {getUnitLabel(units)}</span>
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
          {#key `${selectedMetric}-${lowColor}-${highColor}-${invert}`}
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
              colors={metricColors}
              {COLOR_PALETTES}
              {onCustomColorsChange}
              {invert}
              {onInvertChange}
              {transparencyPercent}
              {onTransparencyChange}
            />
          {/key}
        {:else}
          <!-- Default simple scale preview -->
          <div class="w-full text-xs text-muted-foreground space-y-1.5">
            <span
              class="block h-3.5 w-full rounded-sm bg-linear-to-r from-(--metric-color)/15 to-(--metric-color)"
              style:--metric-color="var({cssVar})"
            ></span>
            <div class="flex justify-between text-[11px] tabular-nums">
              <span>0 {metricUnit}</span>
              <span class="text-muted-foreground">{metricLabel} default scale</span>
              <span>{metricMax} {metricUnit}</span>
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </div>
