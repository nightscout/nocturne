<script lang="ts">
  import { Rule } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";

  interface Props {
    level: "high" | "low" | "veryHigh" | "veryLow" | "targetLow" | "targetHigh";
    class?: string;
    strokeDasharray?: string;
  }

  let { level, class: className = "", strokeDasharray = "4,4" }: Props = $props();

  const ctx = getGlucoseChartContext();
  // Use the same glucose scale as GlucoseTrack's Spline — maps threshold
  // into the chart's y-domain so it aligns with the glucose line. Target
  // levels are nullable (no profile) — skip rendering when absent.
  const value = $derived(ctx.engine.thresholds[level]);
  const y = $derived(value == null ? null : ctx.layout.glucose.scale(value));
</script>

{#if y != null}
  <Rule {y} class={className} stroke-dasharray={strokeDasharray} />
{/if}
