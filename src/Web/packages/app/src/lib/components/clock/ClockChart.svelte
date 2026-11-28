<!--
  A clock face's chart element, drawn from the chart window the face's data source holds rather than
  from the realtime store, so it draws the same on a public clock link as in the editor.
-->
<script lang="ts">
  import { browser } from "$app/env";
  import {
    chartLineColor,
    chartLineColorMode,
    predictionDisplayMode,
    predictionMinutes,
    type GlucoseUnits,
  } from "#lib/stores/appearance-store.svelte.js";
  import GlucoseChartShell from "#lib/components/dashboard/glucose-chart/GlucoseChartShell.svelte";
  import { mergeRealtimeGlucose } from "#lib/components/dashboard/glucose-chart/engine/merge-glucose.js";
  import {
    BasalTrack,
    createChartDataView,
    DeviceEventMarkers,
    GlucoseTrack,
    IobCobTrack,
    PredictionTrack,
    ThresholdRules,
    TrackerMarkers,
    type LegendState,
  } from "@nocturne/charts";
  import type { ClockChartConfig } from "#lib/api/index.js";
  import { DEFAULT_ELEMENT_HOURS, getClockFaceDataSource } from "./clock-face-data.svelte";

  interface Props {
    chartConfig: ClockChartConfig | undefined;
    hours: number | undefined;
    glucoseUnits: GlucoseUnits;
  }

  let { chartConfig, hours, glucoseUnits }: Props = $props();

  const source = getClockFaceDataSource();
  const HOUR_MS = 60 * 60 * 1000;

  let now = $state(Date.now());
  $effect(() => {
    if (!browser) return;
    const interval = setInterval(() => (now = Date.now()), 30_000);
    return () => clearInterval(interval);
  });

  const nowMinute = $derived(Math.floor(now / 60000) * 60000);
  const lookbackHours = $derived(hours || DEFAULT_ELEMENT_HOURS);
  const showPredictions = $derived(
    (chartConfig?.showPredictions ?? false) && source.predictions !== null
  );
  const displayDateRange = $derived({
    from: new Date(nowMinute - lookbackHours * HOUR_MS),
    to: new Date(nowMinute),
  });
  const withPredictions = $derived({
    from: displayDateRange.from,
    to: showPredictions
      ? new Date(nowMinute + predictionMinutes.current * 60 * 1000)
      : displayDateRange.to,
  });

  const view = createChartDataView({
    get serverChartData() {
      return source.chart;
    },
    get glucoseData() {
      return mergeRealtimeGlucose(
        source.chart,
        [],
        displayDateRange.from.getTime(),
        displayDateRange.to.getTime()
      );
    },
    get predictionData() {
      return source.predictions;
    },
    predictionError: null,
    chartDataError: null,
    get predictionServiceAvailable() {
      return source.predictions !== null;
    },
    get predictionAvailability() {
      return source.predictions !== null ? "available" : "unavailable";
    },
    get effectiveShowPredictions() {
      return showPredictions;
    },
    get nowMinute() {
      return nowMinute;
    },
    get lookbackHours() {
      return lookbackHours;
    },
    get fullDataRange() {
      return displayDateRange;
    },
    get displayDateRange() {
      return displayDateRange;
    },
    get displayDateRangeWithPredictions() {
      return withPredictions;
    },
    get fullXDomain() {
      return withPredictions;
    },
  });

  // The tracks this chart draws. The face's chart window holds the tracks of every chart on the
  // face, so each chart picks its own out of it.
  const legend = $derived<LegendState>({
    iob: chartConfig?.showIob ?? false,
    cob: chartConfig?.showCob ?? false,
    basal: chartConfig?.showBasal ?? false,
    bolus: chartConfig?.showBolus ?? true,
    carbs: chartConfig?.showCarbs ?? true,
    deviceEvents: chartConfig?.showDeviceEvents ?? false,
    alarms: false,
    scheduledTrackers: chartConfig?.showTrackers ?? false,
    basalInjections: false,
    overrideSpans: false,
    profileSpans: false,
    activitySpans: false,
    pumpModes: false,
    expandedPumpModes: false,
    toggle() {},
  });
  const drawsOnBoard = $derived(legend.bolus || legend.carbs || legend.iob || legend.cob);
</script>

<GlucoseChartShell engine={view} {legend} heightClass="h-full" {glucoseUnits}>
  {#snippet tracks()}
    {#if chartConfig?.showBasal ?? false}
      <BasalTrack />
    {/if}
    <ThresholdRules />
    <GlucoseTrack
      showPoints={false}
      lineColorMode={chartLineColorMode.current}
      lineColor={chartLineColor.current}
    />
    {#if showPredictions}
      <PredictionTrack displayMode={predictionDisplayMode.current} enabled />
    {/if}
    {#if drawsOnBoard}
      <IobCobTrack />
    {/if}
    {#if chartConfig?.showDeviceEvents ?? false}
      <DeviceEventMarkers />
    {/if}
    {#if chartConfig?.showTrackers ?? false}
      <TrackerMarkers />
    {/if}
  {/snippet}
</GlucoseChartShell>
