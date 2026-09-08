<script lang="ts">
  import { untrack } from "svelte";
  import type { ColorFocusRange as Range } from "$lib/utils/metric-color-focus";
  import ColorFocusRange from "./ColorFocusRange.svelte";

  let {
    initialRange = null,
    observedMax = 500,
    fixedMax,
    metricLabel = "TDD",
    unit = "U",
  }: {
    initialRange?: Range | null;
    observedMax?: number;
    fixedMax?: number;
    metricLabel?: string;
    unit?: string;
  } = $props();

  let range = $state<Range | null>(untrack(() => initialRange));
</script>

<ColorFocusRange
  {metricLabel}
  {unit}
  {observedMax}
  {fixedMax}
  cssVar="--primary"
  focusRange={range}
  onFocusRangeChange={(next) => (range = next)}
/>
<output data-testid="selected-range">{JSON.stringify(range)}</output>
