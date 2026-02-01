<script lang="ts">
  import { Area, Axis, Text, ChartClipPath, Highlight } from "layerchart";
  import { curveMonotoneX, bisector } from "d3";
  import type { ScaleLinear } from "d3-scale";
  import type { Treatment } from "$lib/api";
  import BolusMarker from "../markers/BolusMarker.svelte";
  import CarbMarker from "../markers/CarbMarker.svelte";

  interface SeriesDataPoint {
    time: Date;
    value: number;
  }

  interface BolusMarkerData {
    time: Date;
    insulin: number;
    treatment: Treatment;
  }

  interface CarbMarkerData {
    time: Date;
    carbs: number;
    treatment: Treatment;
    label: string | null;
    isOffset?: boolean;
  }

  interface Props {
    iobData: SeriesDataPoint[];
    cobData: SeriesDataPoint[];
    carbRatio: number;
    iobScale: (value: number) => number;
    iobZero: number;
    iobAxisScale: ScaleLinear<number, number>;
    iobTrackTop: number;
    showIob: boolean;
    showCob: boolean;
    showBolus: boolean;
    showCarbs: boolean;
    bolusMarkers: BolusMarkerData[];
    carbMarkers: CarbMarkerData[];
    context: { xScale: (time: Date) => number; yScale: (value: number) => number };
    onMarkerClick: (treatment: Treatment) => void;
    showIobTrack: boolean;
  }

  let {
    iobData,
    cobData,
    carbRatio,
    iobScale,
    iobZero,
    iobAxisScale,
    iobTrackTop,
    showIob,
    showCob,
    showBolus,
    showCarbs,
    bolusMarkers,
    carbMarkers,
    context,
    onMarkerClick,
    showIobTrack,
  }: Props = $props();

  // Bisector for finding nearest data point
  const bisectDate = bisector((d: { time: Date }) => d.time).left;

  function findSeriesValue(
    series: SeriesDataPoint[],
    time: Date
  ): SeriesDataPoint | undefined {
    const i = bisectDate(series, time, 1);
    const d0 = series[i - 1];
    const d1 = series[i];
    if (!d0) return d1;
    if (!d1) return d0;
    return time.getTime() - d0.time.getTime() > d1.time.getTime() - time.getTime()
      ? d1
      : d0;
  }
</script>

{#if showIobTrack}
  <!-- IOB axis on right -->
  <Axis
    placement="right"
    scale={iobAxisScale}
    ticks={2}
    tickLabelProps={{ class: "text-[9px] fill-muted-foreground" }}
  />

  <!-- IOB/COB track label -->
  <Text x={4} y={iobTrackTop + 12} class="text-[8px] fill-muted-foreground font-medium">
    IOB/COB
  </Text>
{/if}

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
    {#each bolusMarkers as marker}
      {@const xPos = context.xScale(marker.time)}
      {@const yPos = context.yScale(iobScale(marker.insulin))}
      <BolusMarker
        {xPos}
        {yPos}
        insulin={marker.insulin}
        treatment={marker.treatment}
        {onMarkerClick}
      />
    {/each}
  {/if}

  <!-- Carb markers -->
  {#if showCarbs}
    {#each carbMarkers as marker}
      {@const xPos = context.xScale(marker.time)}
      {@const yPos = context.yScale(iobScale(marker.carbs / carbRatio))}
      <CarbMarker
        {xPos}
        {yPos}
        carbs={marker.carbs}
        label={marker.label}
        treatment={marker.treatment}
        {onMarkerClick}
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
    />
  {/if}
</ChartClipPath>
