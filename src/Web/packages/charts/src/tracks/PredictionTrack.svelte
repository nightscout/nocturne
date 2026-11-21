<script lang="ts">
  import { ChartClipPath } from "layerchart";
  import { getGlucoseChartContext } from "../context.svelte.js";
  import PredictionVisualizations from "./PredictionVisualizations.svelte";
  import type { PredictionDisplayMode } from "../predictions.js";

  interface Props {
    displayMode?: PredictionDisplayMode;
    /** The viewer's switch for forecasts; off draws nothing. */
    enabled?: boolean;
    /** "line" (default) or Nightscout's hollow "dots". */
    markStyle?: "line" | "dots";
    /** Radius of a forecast dot. */
    dotRadius?: number;
  }

  let { displayMode = "cone", enabled = true, markStyle, dotRadius }: Props = $props();

  const ctx = getGlucoseChartContext();

  const showPredictions = $derived(
    ctx.view.predictionServiceAvailable && ctx.view.predictionData !== null
  );
</script>

<ChartClipPath>
  <PredictionVisualizations
    {showPredictions}
    predictionData={ctx.view.predictionData}
    predictionEnabled={enabled}
    predictionDisplayMode={displayMode}
    predictionError={ctx.view.predictionError}
    glucoseScale={ctx.layout.glucose.scale}
    glucoseTrackTop={ctx.layout.glucose.top}
    chartXDomain={{
      from: ctx.view.displayDateRange.from,
      to: ctx.view.displayDateRangeWithPredictions.to,
    }}
    glucoseData={ctx.view.glucoseData}
    labels={ctx.labels}
    {markStyle}
    {dotRadius}
  />
</ChartClipPath>
