<script lang="ts">
  import { Rule } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  interface Props {
    level: "high" | "low" | "veryHigh" | "veryLow";
    class?: string;
    strokeDasharray?: string;
  }

  let { level, class: className = "", strokeDasharray = "4,4" }: Props = $props();

  const ctx = getGlucoseChartContext();
  // Use the same glucose scale as GlucoseTrack's Spline — maps threshold
  // into the chart's y-domain so it aligns with the glucose line.
  const y = $derived(ctx.layout.glucose.scale(ctx.engine.thresholds[level]));
</script>

<Rule {y} class={className} stroke-dasharray={strokeDasharray} />
