<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import PredictionVisualizations from "../../PredictionVisualizations.svelte";
  import {
    predictionEnabled,
    predictionDisplayMode,
    type PredictionDisplayMode,
  } from "$lib/stores/appearance-store.svelte";

  interface Props {
    displayMode?: PredictionDisplayMode;
  }

  let { displayMode }: Props = $props();

  const ctx = getGlucoseChartContext();

  const showPredictions = $derived(
    ctx.engine.predictionServiceAvailable && ctx.engine.predictionData !== null
  );
  const effectiveDisplayMode = $derived(
    displayMode ?? predictionDisplayMode.current
  );
</script>

<ChartClipPath>
  <PredictionVisualizations
    {showPredictions}
    predictionData={ctx.engine.predictionData}
    predictionEnabled={predictionEnabled.current}
    predictionDisplayMode={effectiveDisplayMode}
    predictionError={ctx.engine.predictionError}
    glucoseScale={ctx.layout.glucose.scale}
    glucoseTrackTop={ctx.layout.glucose.top}
    chartXDomain={{
      from: ctx.engine.displayDateRange.from,
      to: ctx.engine.displayDateRangeWithPredictions.to,
    }}
    glucoseData={ctx.engine.glucoseData}
  />
</ChartClipPath>
