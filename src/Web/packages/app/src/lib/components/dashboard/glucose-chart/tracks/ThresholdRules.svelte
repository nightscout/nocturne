<script lang="ts">
  import { Rule } from "layerchart";
  import { tv } from "tailwind-variants";
  import ThresholdRule from "./ThresholdRule.svelte";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import { dashOf } from "#lib/components/charts/print/chart-print-patterns.js";
  import { tryGetRealtimeStore } from "#lib/stores/realtime-store.svelte.js";

  interface Props {
    /**
     * "nightscout" draws cgm-remote-monitor's rules (chart.js): the urgent limits dotted
     * (1,6), the range limits dashed (3,3), a dashed line at now, and no personal target.
     */
    variant?: "default" | "nightscout";
  }

  let { variant = "default" }: Props = $props();

  const ctx = getGlucoseChartContext();
  // A server render (PlotSnapshot) has no realtime store; it draws "now" at the render time.
  const realtimeStore = tryGetRealtimeStore();
  const targetLow = $derived(ctx.engine.thresholds.targetLow);
  const targetHigh = $derived(ctx.engine.thresholds.targetHigh);

  const rule = tv({
    variants: {
      kind: {
        high: "stroke-glucose-high/50 print:stroke-foreground/60",
        low: "stroke-glucose-very-low/50 print:stroke-foreground/60",
        target: "stroke-glucose-in-range/70 print:stroke-foreground/40",
        nightscoutUrgent: "stroke-(--ns-rule-urgent)",
        nightscoutRange: "stroke-(--ns-rule-target)",
      },
    },
  });
</script>

{#if variant === "nightscout"}
  <ThresholdRule level="veryHigh" class={rule({ kind: "nightscoutUrgent" })} strokeDasharray="1,6" />
  <ThresholdRule level="high" class={rule({ kind: "nightscoutRange" })} strokeDasharray="3,3" />
  <ThresholdRule level="low" class={rule({ kind: "nightscoutRange" })} strokeDasharray="3,3" />
  <ThresholdRule level="veryLow" class={rule({ kind: "nightscoutUrgent" })} strokeDasharray="1,6" />
  <Rule x={new Date(realtimeStore?.now ?? Date.now())} class={rule({ kind: "nightscoutRange" })} dashArray="3,3" />
{:else}
  <!-- Fixed clinical in-range band boundaries. -->
  <ThresholdRule level="high" class={rule({ kind: "high" })} />
  <ThresholdRule level="low" class={rule({ kind: "low" })} />

  <!-- Personal target (from the active profile), drawn distinctly. A point
       target (low === high) renders as a single line. -->
  {#if targetLow != null}
    <ThresholdRule
      level="targetLow"
      class={rule({ kind: "target" })}
      strokeDasharray={dashOf("glucose-target")}
    />
  {/if}
  {#if targetHigh != null && targetHigh !== targetLow}
    <ThresholdRule
      level="targetHigh"
      class={rule({ kind: "target" })}
      strokeDasharray={dashOf("glucose-target")}
    />
  {/if}
{/if}
