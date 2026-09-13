<script lang="ts">
  import * as Select from "$lib/components/ui/select";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
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
    { label: "Theme Default", low: undefined, high: undefined },
    { label: "Cool Blue → Hot Red", low: "#3b82f6", high: "#ef4444" },
    { label: "Teal → Amber", low: "#14b8a6", high: "#f59e0b" },
    { label: "Indigo → Rose", low: "#6366f1", high: "#f43f5e" },
    { label: "Emerald → Violet", low: "#10b981", high: "#8b5cf6" },
    { label: "Cyan → Orange", low: "#06b6d4", high: "#ea580c" },
    { label: "Slate → Lime", low: "#64748b", high: "#84cc16" },
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

  let transparencyInput = $state(transparencyPercent);
  $effect(() => {
    transparencyInput = transparencyPercent;
  });

  function handleTransparencyInput(event: Event & { currentTarget: HTMLInputElement }) {
    const val = event.currentTarget.valueAsNumber;
    if (Number.isFinite(val)) {
      onTransparencyChange(Math.max(0, Math.min(100, val)));
    }
  }
</script>

<div class="mb-6 rounded-lg border border-border bg-card p-3 space-y-3">
  <!-- Top bar: Metric Selector & Advanced Options Toggle -->
  <div class="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 border-b border-border/40 pb-2.5">
    <div class="flex items-center gap-3">
      <Select.Root
        type="single"
        value={selectedMetric}
        onValueChange={(v) => {
          if (v) selectedMetric = v as HeatmapMetric;
        }}
      >
        <Select.Trigger class="w-[160px] h-8 text-xs print:hidden">
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
      <div class="flex items-center gap-1.5 text-xs text-muted-foreground">
        <span
          class="inline-block h-3 w-3 rounded-sm"
          style="background: var(--muted)"
        ></span>
        {selectedMetric === "avgGlucose" ? "Other Data (no glucose)" : `No ${METRIC_OPTIONS.find((o) => o.value === selectedMetric)?.label.toLowerCase()} data`}
      </div>
    </div>

    <!-- Advanced Settings Checkbox -->
    <div class="flex items-center gap-2 text-xs">
      <label class="flex items-center gap-2 font-medium cursor-pointer text-foreground/90">
        <input
          type="checkbox"
          checked={advancedMode}
          onchange={(e) => onAdvancedModeChange(e.currentTarget.checked)}
          class="h-4 w-4 rounded border-border text-primary focus:ring-primary/40 cursor-pointer"
        />
        Advanced settings
      </label>
    </div>
  </div>

  <!-- Advanced Controls Banner (when enabled) -->
  {#if advancedMode}
    <div class="p-2.5 rounded-md bg-muted/40 border border-border/50 text-xs flex flex-wrap items-center gap-x-6 gap-y-2.5">
      <!-- Out of band transparency input -->
      <div class="flex items-center gap-2">
        <label for="out-of-band-transparency" class="font-medium text-foreground/80">
          Out-of-band transparency:
        </label>
        <div class="flex items-center gap-1">
          <Input
            id="out-of-band-transparency"
            type="number"
            min={0}
            max={100}
            step={1}
            bind:value={transparencyInput}
            oninput={handleTransparencyInput}
            class="h-7 w-16 px-2 text-xs tabular-nums"
          />
          <span class="text-muted-foreground">%</span>
        </div>
      </div>

      <!-- Color Palette (for non-glucose metrics) -->
      {#if selectedMetric !== "avgGlucose"}
        <div class="flex items-center gap-2">
          <span class="font-medium text-foreground/80">Color palette:</span>
          <div class="flex items-center gap-1.5 flex-wrap">
            {#each COLOR_PALETTES as pal}
              {@const isSelected = lowColor === pal.low && highColor === pal.high}
              <Button
                variant={isSelected ? "secondary" : "outline"}
                size="sm"
                class="h-7 px-2 text-xs gap-1.5"
                onclick={() => onCustomColorsChange(pal.low, pal.high)}
              >
                {#if pal.low && pal.high}
                  <span
                    class="inline-block size-3 rounded-full border border-black/20"
                    style:background="linear-gradient(to right, {pal.low}, {pal.high})"
                  ></span>
                {/if}
                {pal.label}
              </Button>
            {/each}
          </div>
        </div>
      {/if}
    </div>
  {/if}

  <!-- Sliders & Ranges -->
  {#if selectedMetric === "avgGlucose"}
    <div class="w-full">
      {#if advancedMode}
        <ColorFocusRange
          glucose
          {units}
          thresholds={glucoseThresholds}
          stops={HEATMAP_STOPS}
          onThresholdsChange={onGlucoseThresholdsChange}
          {focusBand}
          onFocusBandChange={onFocusBandChange}
        />
      {:else}
        <!-- Default simple scale preview -->
        <div class="w-full max-w-[460px] text-xs text-muted-foreground space-y-1.5">
          <span
            class="block h-3.5 w-full rounded-sm"
            style:background="linear-gradient(to right in srgb, {HEATMAP_STOPS.map((s) => `${s.color} ${((s.mgdl - 40) / (350 - 40)) * 100}%`).join(', ')})"
          ></span>
          <div class="flex justify-between text-[11px] tabular-nums">
            <span>40 {units === "mmol" ? "mmol/L" : "mg/dL"}</span>
            <span class="text-muted-foreground">Default color scale</span>
            <span>350 {units === "mmol" ? "mmol/L" : "mg/dL"}</span>
          </div>
        </div>
      {/if}
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
    <div class="w-full">
      {#if advancedMode}
        {#key `${selectedMetric}-${lowColor}-${highColor}`}
          <ColorFocusRange
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
          />
        {/key}
      {:else}
        <!-- Default simple scale preview -->
        <div class="w-full max-w-[460px] text-xs text-muted-foreground space-y-1.5">
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
