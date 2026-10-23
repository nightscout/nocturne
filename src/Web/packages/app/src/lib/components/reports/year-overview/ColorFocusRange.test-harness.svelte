<script lang="ts">
  import { untrack } from "svelte";
  import type { GlucoseUnits } from "$lib/utils/formatting";
  import {
    glucoseScale,
    intensityScale,
    GLUCOSE_LIMITS,
    type ColorBoundary,
    type ColorFocusRange as Range,
  } from "$lib/utils/metric-color-focus";
  import ColorFocusRange from "./ColorFocusRange.svelte";

  let {
    initialWindow = null,
    initialBoundaries = null,
    observedMax = 500,
    fixedMax,
    metricLabel = "TDD",
    unit = "U",
    glucose = false,
    units = "mg/dl",
  }: {
    initialWindow?: Range | null;
    initialBoundaries?: ColorBoundary[] | null;
    observedMax?: number;
    fixedMax?: number;
    metricLabel?: string;
    unit?: string;
    glucose?: boolean;
    units?: GlucoseUnits;
  } = $props();

  let focusWindow = $state<Range | null>(untrack(() => initialWindow));
  let boundaries = $state<ColorBoundary[] | null>(untrack(() => initialBoundaries));
  let dim = $state(90);
  const limits = $derived<Range>(glucose ? GLUCOSE_LIMITS : [0, fixedMax ?? Infinity]);
  const scale = $derived(
    glucose
      ? glucoseScale(focusWindow, boundaries, dim)
      : intensityScale("--insulin", limits, observedMax, focusWindow, boundaries, dim)
  );
</script>

<ColorFocusRange
  {metricLabel}
  {scale}
  {limits}
  {glucose}
  {units}
  {unit}
  onWindowChange={(next) => (focusWindow = next)}
  onBoundariesChange={(next) => (boundaries = next)}
  onDimChange={(next) => (dim = next)}
  onReset={() => {
    focusWindow = null;
    boundaries = null;
  }}
/>
<output data-testid="selected-window">{JSON.stringify(focusWindow)}</output>
<output data-testid="selected-boundaries">{JSON.stringify(boundaries)}</output>
