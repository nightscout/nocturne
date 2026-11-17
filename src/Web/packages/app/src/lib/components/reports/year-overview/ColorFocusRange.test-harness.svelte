<script lang="ts">
  import { untrack } from "svelte";
  import type { GlucoseUnits } from "#lib/utils/formatting.js";
  import {
    glucoseScale,
    intensityScale,
    GLUCOSE_LIMITS,
    type ColorBoundary,
    type ColorEnds,
    type ColorFocusRange as Range,
  } from "#lib/utils/metric-color-focus.js";
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
  let ends = $state<ColorEnds>({});
  let dim = $state(90);
  const limits = $derived<Range>(glucose ? GLUCOSE_LIMITS : [0, fixedMax ?? Infinity]);
  const scale = $derived(
    glucose
      ? glucoseScale(focusWindow, boundaries, dim, ends)
      : intensityScale("--insulin", limits, observedMax, focusWindow, boundaries, dim, ends)
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
  onEndsChange={(next) => (ends = next)}
  onDimChange={(next) => (dim = next)}
  onReset={() => {
    focusWindow = null;
    boundaries = null;
    ends = {};
  }}
/>
<output data-testid="selected-window">{JSON.stringify(focusWindow)}</output>
<output data-testid="selected-boundaries">{JSON.stringify(boundaries)}</output>
<output data-testid="selected-ends">{JSON.stringify(ends)}</output>
