<script lang="ts">
  import { Area, Spline, getChartContext } from "layerchart";
  import { curveMonotoneX } from "d3";
  import type { PredictionData } from "$api/predictions.remote";
  import type { PredictionDisplayMode } from "$lib/stores/appearance-store.svelte";

  interface Props {
    showPredictions: boolean;
    predictionData: PredictionData | null;
    predictionEnabled: boolean;
    predictionDisplayMode: PredictionDisplayMode;
    predictionError: string | null;
    glucoseScale: (v: number) => number;
    glucoseTrackTop: number;
    chartXDomain: { from: Date; to: Date };
    glucoseData: { time: Date; sgv: number }[];
  }

  let {
    showPredictions,
    predictionData,
    predictionEnabled,
    predictionDisplayMode,
    predictionError,
    glucoseScale,
    glucoseTrackTop,
    chartXDomain,
    glucoseData,
  }: Props = $props();

  const chartCtx = getChartContext();

  const predictionEndTime = $derived(chartXDomain.to.getTime());

  const predictionCurveData = $derived(
    predictionData?.curves.main
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      })) ?? []
  );

  const iobPredictionData = $derived(
    predictionData?.curves.iobOnly
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      })) ?? []
  );

  const uamPredictionData = $derived(
    predictionData?.curves.uam
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      })) ?? []
  );

  const cobPredictionData = $derived(
    predictionData?.curves.cob
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      })) ?? []
  );

  const zeroTempPredictionData = $derived(
    predictionData?.curves.zeroTemp
      .filter((p) => p.timestamp <= predictionEndTime)
      .map((p) => ({
        time: new Date(p.timestamp),
        sgv: p.value,
      })) ?? []
  );

  // Prediction cone data (filtered to prediction window)
  const predictionConeData = $derived.by(() => {
    if (!predictionData) return [];

    const curves = [
      predictionData.curves.main,
      predictionData.curves.iobOnly,
      predictionData.curves.zeroTemp,
      predictionData.curves.uam,
      predictionData.curves.cob,
    ].filter((c) => c && c.length > 0);

    if (curves.length === 0) return [];

    const primaryCurve = curves[0];
    return primaryCurve
      .filter((point) => point.timestamp <= predictionEndTime)
      .map((point, i) => {
        const valuesAtTime = curves.map((c) => c[i]?.value ?? point.value);
        return {
          time: new Date(point.timestamp),
          min: Math.min(...valuesAtTime),
          max: Math.max(...valuesAtTime),
          mid: (Math.min(...valuesAtTime) + Math.max(...valuesAtTime)) / 2,
        };
      });
  });
</script>

<svelte:boundary>
  {#snippet pending()}
    <Spline
      data={[
        {
          time: chartXDomain.to,
          sgv: glucoseData.at(-1)?.sgv ?? 100,
        },
        {
          time: new Date(chartXDomain.to.getTime() + 30 * 60 * 1000),
          sgv: glucoseData.at(-1)?.sgv ?? 100,
        },
      ]}
      x={(d) => d.time}
      y={(d) => glucoseScale(d.sgv)}
      curve={curveMonotoneX}
      class="stroke-muted-foreground/50 stroke-1 fill-none animate-pulse"
      stroke-dasharray="4,4"
    />
    <text
      x={chartCtx.xScale(new Date(chartXDomain.to.getTime() + 5 * 60 * 1000))}
      y={chartCtx.yScale(glucoseScale(glucoseData.at(-1)?.sgv ?? 100))}
      dy="-0.355em"
      class="text-[9px] fill-muted-foreground animate-pulse"
    >
      Loading predictions...
    </text>
  {/snippet}

  {#snippet failed(error)}
    <text
      x={50}
      y={glucoseTrackTop + 20}
      dy="-0.355em"
      class="text-xs fill-red-400"
    >
      Prediction unavailable: {error instanceof Error ? error.message : "Error"}
    </text>
  {/snippet}

  {#if showPredictions && predictionEnabled && predictionData}
    {#if predictionDisplayMode === "cone" && predictionConeData.length > 0}
      <Area
        data={predictionConeData}
        x={(d) => d.time}
        y0={(d) => glucoseScale(d.max)}
        y1={(d) => glucoseScale(d.min)}
        curve={curveMonotoneX}
        class="fill-purple-500/20 stroke-none"
        motion="spring"
      />
      <Spline
        data={predictionConeData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.mid)}
        curve={curveMonotoneX}
        motion="spring"
        class="stroke-purple-400 stroke-1 fill-none"
        stroke-dasharray="4,2"
      />
    {:else if predictionDisplayMode === "lines"}
      {#if predictionCurveData.length > 0}
        <Spline
          data={predictionCurveData}
          x={(d) => d.time}
          y={(d) => glucoseScale(d.sgv)}
          curve={curveMonotoneX}
          motion="spring"
          class="stroke-purple-400 stroke-2 fill-none"
          stroke-dasharray="6,3"
        />
      {/if}
      {#if iobPredictionData.length > 0}
        <Spline
          data={iobPredictionData}
          x={(d) => d.time}
          y={(d) => glucoseScale(d.sgv)}
          curve={curveMonotoneX}
          motion="spring"
          class="stroke-cyan-400 stroke-1 fill-none opacity-80"
          stroke-dasharray="4,2"
        />
      {/if}
      {#if zeroTempPredictionData.length > 0}
        <Spline
          data={zeroTempPredictionData}
          x={(d) => d.time}
          y={(d) => glucoseScale(d.sgv)}
          curve={curveMonotoneX}
          motion="spring"
          class="stroke-orange-400 stroke-1 fill-none opacity-80"
          stroke-dasharray="4,2"
        />
      {/if}
      {#if uamPredictionData.length > 0}
        <Spline
          data={uamPredictionData}
          x={(d) => d.time}
          y={(d) => glucoseScale(d.sgv)}
          curve={curveMonotoneX}
          motion="spring"
          class="stroke-green-400 stroke-1 fill-none opacity-80"
          stroke-dasharray="4,2"
        />
      {/if}
      {#if cobPredictionData.length > 0}
        <Spline
          data={cobPredictionData}
          x={(d) => d.time}
          y={(d) => glucoseScale(d.sgv)}
          motion="spring"
          curve={curveMonotoneX}
          class="stroke-yellow-400 stroke-1 fill-none opacity-80"
          stroke-dasharray="4,2"
        />
      {/if}
    {:else if predictionDisplayMode === "main" && predictionCurveData.length > 0}
      <Spline
        data={predictionCurveData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        motion="spring"
        curve={curveMonotoneX}
        class="stroke-purple-400 stroke-2 fill-none"
        stroke-dasharray="6,3"
      />
    {:else if predictionDisplayMode === "iob" && iobPredictionData.length > 0}
      <Spline
        data={iobPredictionData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        motion="spring"
        curve={curveMonotoneX}
        class="stroke-cyan-400 stroke-2 fill-none"
        stroke-dasharray="6,3"
      />
    {:else if predictionDisplayMode === "zt" && zeroTempPredictionData.length > 0}
      <Spline
        data={zeroTempPredictionData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        motion="spring"
        curve={curveMonotoneX}
        class="stroke-orange-400 stroke-2 fill-none"
        stroke-dasharray="6,3"
      />
    {:else if predictionDisplayMode === "uam" && uamPredictionData.length > 0}
      <Spline
        data={uamPredictionData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        motion="spring"
        curve={curveMonotoneX}
        class="stroke-green-400 stroke-2 fill-none"
        stroke-dasharray="6,3"
      />
    {:else if predictionDisplayMode === "cob" && cobPredictionData.length > 0}
      <Spline
        data={cobPredictionData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.sgv)}
        motion="spring"
        curve={curveMonotoneX}
        class="stroke-yellow-400 stroke-2 fill-none"
        stroke-dasharray="6,3"
      />
    {/if}
  {/if}
  {#if showPredictions && predictionError}
    <text
      x={50}
      y={glucoseTrackTop + 20}
      dy="-0.355em"
      class="text-xs fill-red-400"
    >
      Prediction unavailable
    </text>
  {/if}
</svelte:boundary>
