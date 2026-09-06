<script lang="ts">
  import ThresholdRule from "./ThresholdRule.svelte";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  const ctx = getGlucoseChartContext();
  const targetLow = $derived(ctx.engine.thresholds.targetLow);
  const targetHigh = $derived(ctx.engine.thresholds.targetHigh);
</script>

<!-- Fixed clinical in-range band boundaries. -->
<ThresholdRule level="high" class="stroke-glucose-high/50" />
<ThresholdRule level="low" class="stroke-glucose-very-low/50" />

<!-- Personal target (from the active profile), drawn distinctly. A point
     target (low === high) renders as a single line. -->
{#if targetLow != null}
  <ThresholdRule
    level="targetLow"
    class="stroke-glucose-in-range/70"
    strokeDasharray="2,4"
  />
{/if}
{#if targetHigh != null && targetHigh !== targetLow}
  <ThresholdRule
    level="targetHigh"
    class="stroke-glucose-in-range/70"
    strokeDasharray="2,4"
  />
{/if}
