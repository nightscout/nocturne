<script lang="ts">
  import { Area, Circle, Spline, getChartContext } from "layerchart";
  import { curveMonotoneX } from "d3";
  import type { PredictionData, PredictionDisplayMode } from "../predictions.js";
  import type { ChartLabels } from "../labels.js";

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
    /**
     * "line" draws each forecast as a dashed curve; "dots" as hollow 2px-stroked dots,
     * one per forecast step, as cgm-remote-monitor does.
     */
    markStyle?: "line" | "dots";
    /** Radius of a forecast dot. */
    dotRadius?: number;
    labels: Pick<ChartLabels, "loadingPredictions" | "predictionsUnavailable">;
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
    markStyle = "line",
    dotRadius = 3,
    labels,
  }: Props = $props();

  /** Nightscout leaves forecast points under 13 mg/dL undrawn. */
  const MIN_DOT_MGDL = 13;

  type CurveTone = "main" | "iob" | "zt" | "uam" | "cob";
  type CurveWeight = "strong" | "faint";

  // A forecast in focus is drawn strong; the others beside it in the "lines" mode, faint.
  // Dots are always stroked 2px at full opacity.
  const CURVE_TONE: Record<CurveTone, string> = {
    main: "stroke-pred-main",
    iob: "stroke-pred-iob",
    zt: "stroke-pred-zt",
    uam: "stroke-pred-uam",
    cob: "stroke-pred-cob",
  };
  const CURVE_WEIGHT: Record<CurveWeight, string> = { strong: "stroke-2", faint: "stroke-1 opacity-80" };
  function curveMark(tone: CurveTone, weight: CurveWeight, style: "line" | "dots"): string {
    return `fill-none ${CURVE_TONE[tone]} ${style === "dots" ? "stroke-2" : CURVE_WEIGHT[weight]}`;
  }
  const CURVE_DASH: Record<CurveWeight, string> = { strong: "6,3", faint: "4,2" };

  const chartCtx = getChartContext();

  // Set from `onerror` rather than computed in the `failed` snippet. Layerchart's
  // <Chart> remounts its children once mounted, yet Svelte still renders the
  // destroyed boundary's `failed` snippet a microtask later. A call expression
  // there becomes a derived owned by that dead effect. It reads back as an
  // uninitialized symbol and throws from set_text; a plain state read does not.
  // svelte-ignore state_referenced_locally
  let failureMessage = $state(labels.predictionsUnavailable);

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

<!-- Outside the boundary: a snippet declared inside it would be read as one of its props. -->
{#snippet mark(data: { time: Date; sgv: number }[], tone: CurveTone, weight: CurveWeight)}
  {#if markStyle === "dots"}
    <Circle
      data={data.filter((d) => d.sgv >= MIN_DOT_MGDL)}
      key={(d) => d.time.getTime()}
      cx={(d) => d.time}
      cy={(d) => glucoseScale(d.sgv)}
      r={dotRadius}
      class={curveMark(tone, weight, "dots")}
    />
  {:else}
    <Spline
      {data}
      x={(d) => d.time}
      y={(d) => glucoseScale(d.sgv)}
      curve={curveMonotoneX}
      motion="spring"
      class={curveMark(tone, weight, "line")}
      stroke-dasharray={CURVE_DASH[weight]}
    />
  {/if}
{/snippet}

<svelte:boundary onerror={() => (failureMessage = labels.predictionsUnavailable)}>
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
      class="text-2xs fill-muted-foreground animate-pulse"
    >
      {labels.loadingPredictions}
    </text>
  {/snippet}

  {#snippet failed()}
    <text
      x={50}
      y={glucoseTrackTop + 20}
      dy="-0.355em"
      class="text-xs fill-destructive"
    >
      {failureMessage}
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
        class="fill-pred-main/20 stroke-none"
        motion="spring"
      />
      <Spline
        data={predictionConeData}
        x={(d) => d.time}
        y={(d) => glucoseScale(d.mid)}
        curve={curveMonotoneX}
        motion="spring"
        class="stroke-pred-main stroke-1 fill-none"
        stroke-dasharray="4,2"
      />
    {:else if predictionDisplayMode === "lines"}
      {#if predictionCurveData.length > 0}
        {@render mark(predictionCurveData, "main", "strong")}
      {/if}
      {#if iobPredictionData.length > 0}
        {@render mark(iobPredictionData, "iob", "faint")}
      {/if}
      {#if zeroTempPredictionData.length > 0}
        {@render mark(zeroTempPredictionData, "zt", "faint")}
      {/if}
      {#if uamPredictionData.length > 0}
        {@render mark(uamPredictionData, "uam", "faint")}
      {/if}
      {#if cobPredictionData.length > 0}
        {@render mark(cobPredictionData, "cob", "faint")}
      {/if}
    {:else if predictionDisplayMode === "main" && predictionCurveData.length > 0}
      {@render mark(predictionCurveData, "main", "strong")}
    {:else if predictionDisplayMode === "iob" && iobPredictionData.length > 0}
      {@render mark(iobPredictionData, "iob", "strong")}
    {:else if predictionDisplayMode === "zt" && zeroTempPredictionData.length > 0}
      {@render mark(zeroTempPredictionData, "zt", "strong")}
    {:else if predictionDisplayMode === "uam" && uamPredictionData.length > 0}
      {@render mark(uamPredictionData, "uam", "strong")}
    {:else if predictionDisplayMode === "cob" && cobPredictionData.length > 0}
      {@render mark(cobPredictionData, "cob", "strong")}
    {/if}
  {/if}
  {#if showPredictions && predictionError}
    <text
      x={50}
      y={glucoseTrackTop + 20}
      dy="-0.355em"
      class="text-xs fill-destructive"
    >
      {predictionError}
    </text>
  {/if}
</svelte:boundary>
