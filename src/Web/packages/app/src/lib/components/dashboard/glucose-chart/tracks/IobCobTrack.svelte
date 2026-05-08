<script lang="ts">
  import { Area, Axis, Text, ChartClipPath, Highlight, getChartContext } from "layerchart";
  import { curveMonotoneX } from "d3";
  import BolusMarker from "../markers/BolusMarker.svelte";
  import CarbMarker from "../markers/CarbMarker.svelte";
  import { getGlucoseChartContext } from "../chart-context.svelte";
  import type { SeriesPoint } from "../engine/chart-data-engine.svelte";

  interface Props {
    carbRatio?: number;
    onMarkerClick?: (treatmentId: string) => void;
    onPointClick?: (time: Date) => void;
  }

  let { carbRatio = 15, onMarkerClick, onPointClick }: Props = $props();

  const ctx = getGlucoseChartContext();
  const chartCtx = getChartContext();

  const iobData = $derived(ctx.engine.iobData);
  const cobData = $derived(ctx.engine.cobData);
  const bolusMarkers = $derived(ctx.engine.bolusMarkers);
  const carbMarkers = $derived(ctx.engine.carbMarkers);
  const iobCobLayout = $derived(ctx.layout.iobCob);

  const showIob = $derived(ctx.legend?.iob ?? true);
  const showCob = $derived(ctx.legend?.cob ?? true);
  const showBolus = $derived(ctx.legend?.bolus ?? true);
  const showCarbs = $derived(ctx.legend?.carbs ?? true);



  const effectiveOnPointClick = $derived(
    onPointClick ?? ((time: Date) => ctx.inspection?.inspectFromTrack(time))
  );
  const effectiveOnMarkerClick = $derived(
    onMarkerClick ?? ((_treatmentId: string) => {})
  );

  // Bisector for finding nearest data point
  function findSeriesValue(
    series: SeriesPoint[],
    time: Date
  ): SeriesPoint | undefined {
    if (series.length === 0) return undefined;
    let lo = 0;
    let hi = series.length;
    while (lo < hi) {
      const mid = (lo + hi) >>> 1;
      if (series[mid].time < time) lo = mid + 1;
      else hi = mid;
    }
    const d0 = series[lo - 1];
    const d1 = series[lo];
    if (!d0) return d1;
    if (!d1) return d0;
    return time.getTime() - d0.time.getTime() >
      d1.time.getTime() - time.getTime()
      ? d1
      : d0;
  }
</script>

{#if iobCobLayout}
  {@const iobScale = iobCobLayout.scale}
  {@const iobZero = iobCobLayout.zero}
  {@const iobTrackTop = iobCobLayout.top}
  {@const iobAxisScale = iobCobLayout.axisScale}

  <!-- IOB axis on right -->
  <Axis
    placement="right"
    scale={iobAxisScale}
    ticks={2}
    tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
  />

  <!-- IOB/COB track label -->
  <Text
    x={4}
    y={iobTrackTop + 12}
    class="text-[8px] fill-muted-foreground font-medium"
  >
    IOB/COB
  </Text>

  <ChartClipPath>
    <!-- COB area (scaled by carb ratio to show on IOB-equivalent scale) -->
    {#if cobData.length > 0 && cobData.some((d) => d.value > 0.01) && showCob}
      <Area
        data={cobData}
        x={(d) => d.time}
        y0={() => iobZero}
        y1={(d) => iobScale(d.value / carbRatio)}
        motion="spring"
        curve={curveMonotoneX}
        fill=""
        class="fill-carbs/40"
      />
    {/if}

    <!-- IOB area (grows up from bottom of IOB track) -->
    {#if iobData.length > 0 && iobData.some((d) => d.value > 0.01) && showIob}
      <Area
        data={iobData}
        x={(d) => d.time}
        y0={() => iobZero}
        y1={(d) => iobScale(d.value)}
        motion="spring"
        curve={curveMonotoneX}
        fill=""
        class="fill-iob-basal/60"
      />
    {/if}
  </ChartClipPath>

  <ChartClipPath>
    <!-- Bolus markers -->
    {#if showBolus}
      {#each bolusMarkers as marker (marker.treatmentId)}
        {@const xPos = chartCtx.xScale(marker.time)}
        {@const yPos = chartCtx.yScale(iobScale(marker.insulin ?? 0))}
        <BolusMarker
          {xPos}
          {yPos}
          insulin={marker.insulin ?? 0}
          isOverride={marker.isOverride ?? false}
          treatmentId={marker.treatmentId ?? ""}
          onMarkerClick={effectiveOnMarkerClick}
        />
      {/each}
    {/if}

    <!-- Carb markers -->
    {#if showCarbs}
      {#each carbMarkers as marker (marker.treatmentId)}
        {@const xPos = chartCtx.xScale(marker.time)}
        {@const yPos = chartCtx.yScale(
          iobScale((marker.carbs ?? 0) / carbRatio)
        )}
        <CarbMarker
          {xPos}
          {yPos}
          carbs={marker.carbs ?? 0}
          label={marker.label ?? null}
          treatmentId={marker.treatmentId ?? ""}
          onMarkerClick={effectiveOnMarkerClick}
        />
      {/each}
    {/if}

    <!-- COB highlight with remapped scale (scaled by carb ratio) -->
    {#if showCob}
      <Highlight
        x={(d) => d.time}
        y={(d) => {
          const cob = findSeriesValue(cobData, d.time);
          if (!cob || cob.value <= 0) return null;
          return iobScale(cob.value / carbRatio);
        }}
        points={{ class: "fill-carbs" }}
        onPointClick={effectiveOnPointClick
          ? (_e, { data }) => effectiveOnPointClick(data.time)
          : undefined}
      />
    {/if}

    <!-- IOB highlight with remapped scale -->
    {#if showIob}
      <Highlight
        x={(d) => d.time}
        y={(d) => {
          const iob = findSeriesValue(iobData, d.time);
          if (!iob || iob.value <= 0) return null;
          return iobScale(iob.value);
        }}
        points={{ class: "fill-iob-basal" }}
        onPointClick={effectiveOnPointClick
          ? (_e, { data }) => effectiveOnPointClick(data.time)
          : undefined}
      />
    {/if}
  </ChartClipPath>
{/if}
