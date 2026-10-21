<script lang="ts">
  import { Spline, ChartClipPath } from "layerchart";
  import { curveMonotoneX } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import { PREDICTION_CURVES, type PredictionCurves } from "../predictions.js";

  interface Props {
    predictions: PredictionCurves;
    glucoseScale: ScaleLinear<number, number>;
  }

  let { predictions, glucoseScale }: Props = $props();

  const stepMs = $derived((predictions.intervalMinutes ?? 5) * 60 * 1000);
  const start = $derived(predictions.start.getTime());

  // Drawn in PREDICTION_CURVES order so the curve oref acts on most often
  // (IOB) sits on top of the others.
  const lines = $derived(
    PREDICTION_CURVES.flatMap((curve) => {
      const values = predictions.curves[curve.key];
      if (!values || values.length === 0) return [];
      return [
        {
          ...curve,
          points: values.map((sgv, i) => ({ time: new Date(start + i * stepMs), sgv })),
        },
      ];
    })
  );
</script>

<ChartClipPath>
  {#each lines as line (line.key)}
    <Spline
      data={line.points}
      x={(d) => d.time}
      y={(d) => glucoseScale(d.sgv)}
      curve={curveMonotoneX}
      class="fill-none stroke-2 {line.class}"
      stroke-dasharray={line.dash}
      stroke-linecap="round"
    />
  {/each}
</ChartClipPath>
