<script lang="ts">
  import type { PredictionData } from "$api/predictions.remote";
  import GlucoseResponseChart from "./GlucoseResponseChart.svelte";

  interface Props {
    glucoseData: { time: Date; sgv: number; color: string }[];
    centerTime: Date;
    predictionData: PredictionData | null;
    highThreshold: number;
    lowThreshold: number;
    beforeMs: number;
    afterMs: number;
    label?: string;
  }

  let {
    glucoseData,
    centerTime,
    predictionData,
    highThreshold,
    lowThreshold,
    beforeMs,
    afterMs,
    label,
  }: Props = $props();

  // Predictions can reach past afterMs, so the window stretches to cover them.
  const windowedGlucoseData = $derived.by(() => {
    const centerMs = centerTime.getTime();
    const minMs = centerMs - beforeMs;
    const horizonMs = centerMs + afterMs;
    const predictionHorizonMs = predictionData?.curves.main.length
      ? Math.max(...predictionData.curves.main.map((p) => p.timestamp))
      : horizonMs;
    const maxMs = Math.max(horizonMs, predictionHorizonMs);
    return glucoseData.filter((d) => {
      const t = d.time.getTime();
      return t >= minMs && t <= maxMs;
    });
  });
</script>

{#if windowedGlucoseData.length > 0}
  <div class="py-2">
    <p class="text-xs text-muted-foreground mb-1">Glucose Response</p>
    <GlucoseResponseChart
      glucoseData={windowedGlucoseData}
      {centerTime}
      {predictionData}
      {highThreshold}
      {lowThreshold}
      {label}
    />
  </div>
{/if}
