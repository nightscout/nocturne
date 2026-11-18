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
    initialRange = null,
    initialBoundaries = null,
    observedMax = 500,
    fixedMax,
    metricLabel = "TDD",
    unit = "U",
    glucose = false,
    units = "mg/dl",
  }: {
    initialWindow?: Range | null;
    initialRange?: Range | null;
    initialBoundaries?: ColorBoundary[] | null;
    observedMax?: number;
    fixedMax?: number;
    metricLabel?: string;
    unit?: string;
    glucose?: boolean;
    units?: GlucoseUnits;
  } = $props();

  let focusWindow = $state<Range | null>(untrack(() => initialWindow));
  let colorRange = $state<Range | null>(untrack(() => initialRange));
  let boundaries = $state<ColorBoundary[] | null>(untrack(() => initialBoundaries));
  let ends = $state<ColorEnds>({});
  let dim = $state(90);
  const limits = $derived<Range>(glucose ? GLUCOSE_LIMITS : [0, fixedMax ?? Infinity]);
  const scale = $derived(
    glucose
      ? glucoseScale(focusWindow, boundaries, dim, ends, colorRange)
      : intensityScale("--insulin", limits, observedMax, focusWindow, boundaries, dim, ends, colorRange)
  );
</script>

<ColorFocusRange
  {metricLabel}
  {scale}
  {limits}
  {glucose}
  {units}
  {unit}
  onWindowChange={(next) => {
    if (!glucose && !colorRange) colorRange = [...scale.domain];
    focusWindow = next;
  }}
  onRangeChange={(next) => (colorRange = next)}
  onBoundariesChange={(next) => (boundaries = next)}
  onEndsChange={(next) => (ends = next)}
  onDimChange={(next) => (dim = next)}
  onReset={() => {
    focusWindow = null;
    colorRange = null;
    boundaries = null;
    ends = {};
  }}
/>
<output data-testid="selected-window">{JSON.stringify(focusWindow)}</output>
<output data-testid="selected-range">{JSON.stringify(colorRange)}</output>
<output data-testid="selected-boundaries">{JSON.stringify(boundaries)}</output>
<output data-testid="selected-ends">{JSON.stringify(ends)}</output>
