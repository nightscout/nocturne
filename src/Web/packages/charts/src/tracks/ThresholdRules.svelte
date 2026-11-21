<script lang="ts">
  import { Rule } from "layerchart";
  import ThresholdRule from "./ThresholdRule.svelte";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import { dashOf } from "../print/patterns.js";

  interface Props {
    /**
     * "nightscout" draws cgm-remote-monitor's rules (chart.js): the urgent limits dotted
     * (1,6), the range limits dashed (3,3), a dashed line at now, and no personal target.
     */
    variant?: "default" | "nightscout";
  }

  let { variant = "default" }: Props = $props();

  const ctx = getGlucoseChartContext();
  const targetLow = $derived(ctx.view.thresholds.targetLow);
  const targetHigh = $derived(ctx.view.thresholds.targetHigh);

  const NIGHTSCOUT_RULE = {
    urgent: "stroke-(--ns-rule-urgent)",
    range: "stroke-(--ns-rule-target)",
  } as const;
</script>

{#if variant === "nightscout"}
  <ThresholdRule level="veryHigh" class={NIGHTSCOUT_RULE.urgent} strokeDasharray="1,6" />
  <ThresholdRule level="high" class={NIGHTSCOUT_RULE.range} strokeDasharray="3,3" />
  <ThresholdRule level="low" class={NIGHTSCOUT_RULE.range} strokeDasharray="3,3" />
  <ThresholdRule level="veryLow" class={NIGHTSCOUT_RULE.urgent} strokeDasharray="1,6" />
  <Rule x={new Date(ctx.view.nowMinute)} class={NIGHTSCOUT_RULE.range} dashArray="3,3" />
{:else}
<!-- Fixed clinical in-range band boundaries. -->
<ThresholdRule level="high" class="stroke-glucose-high/50 print:stroke-foreground/60" />
<ThresholdRule level="low" class="stroke-glucose-very-low/50 print:stroke-foreground/60" />

<!-- Personal target (from the active profile), drawn distinctly. A point
     target (low === high) renders as a single line. -->
{#if targetLow != null}
  <ThresholdRule
    level="targetLow"
    class="stroke-glucose-in-range/70 print:stroke-foreground/40"
    strokeDasharray={dashOf("glucose-target")}
  />
{/if}
{#if targetHigh != null && targetHigh !== targetLow}
  <ThresholdRule
    level="targetHigh"
    class="stroke-glucose-in-range/70 print:stroke-foreground/40"
    strokeDasharray={dashOf("glucose-target")}
  />
{/if}
{/if}
