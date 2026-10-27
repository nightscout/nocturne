<script lang="ts">
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import ThresholdRules from "./tracks/ThresholdRules.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import { createChartDataView } from "./engine/chart-data-view.svelte";
  import { mergeRealtimeGlucose } from "./engine/merge-glucose";
  import type { LegendState } from "./chart-context.svelte";
  import type { AreaMode, LineColorMode } from "./utils/glucose-gradient-stops";
  import { plotPadding } from "./plot-padding";
  import type { TransformedChartData } from "#lib/utils/chart-data-transform.js";
  import { GLUCOSE_CHART_FETCH_HOURS, type GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";

  /**
   * The dashboard's main plot as the live chart first draws it, for the server to render to markup
   * (`#lib/server/plot-snapshot.js`) so the glucose line paints with the document: the same engine
   * view and tracks at the card's starting legend, its window leaving the forecast's room before
   * the forecast itself has loaded. The time axis is left to the live chart, since its ticks fall
   * on the viewer's local hours.
   */
  interface Props {
    chartData: TransformedChartData;
    nowMs: number;
    lookbackHours: number;
    /** Minutes of forecast the window leaves room for, or 0 when the chart draws none. */
    forecastMinutes: number;
    size: { width: number; height: number };
    compact: boolean;
    style: {
      lineColorMode: LineColorMode;
      lineColor: string;
      pointColorMode: LineColorMode | undefined;
      pointColor: string | undefined;
      showPoints: boolean;
      areaMode: AreaMode;
      areaOpacity: number;
      units: GlucoseUnits;
    };
  }

  const { chartData, nowMs, lookbackHours, forecastMinutes, size, compact, style }: Props = $props();

  const HOUR_MS = 60 * 60 * 1000;

  // A server render runs once, so its props are read once.
  // svelte-ignore state_referenced_locally
  const nowMinute = Math.floor(nowMs / 60000) * 60000;
  // svelte-ignore state_referenced_locally
  const displayDateRange = { from: new Date(nowMinute - lookbackHours * HOUR_MS), to: new Date(nowMinute) };
  // svelte-ignore state_referenced_locally
  const withForecast = { from: displayDateRange.from, to: new Date(nowMinute + forecastMinutes * 60 * 1000) };
  const fullDataRange = { from: new Date(nowMinute - GLUCOSE_CHART_FETCH_HOURS * HOUR_MS), to: new Date(nowMinute) };

  // svelte-ignore state_referenced_locally
  const engine = createChartDataView({
    serverChartData: chartData,
    glucoseData: mergeRealtimeGlucose(chartData, [], fullDataRange.from.getTime(), fullDataRange.to.getTime()),
    predictionData: null,
    predictionError: null,
    chartDataError: null,
    predictionServiceAvailable: false,
    predictionAvailability: "unknown",
    effectiveShowPredictions: forecastMinutes > 0,
    nowMinute,
    lookbackHours,
    fullDataRange,
    displayDateRange,
    displayDateRangeWithPredictions: withForecast,
    fullXDomain: fullDataRange,
  });

  // GlucoseChartCard's starting legend, which decides the track layout.
  const legend: LegendState = {
    iob: true,
    cob: true,
    basal: true,
    bolus: true,
    carbs: true,
    deviceEvents: true,
    alarms: true,
    scheduledTrackers: true,
    basalInjections: true,
    overrideSpans: false,
    profileSpans: false,
    activitySpans: false,
    pumpModes: true,
    expandedPumpModes: false,
    toggle() {},
  };
</script>

<GlucoseChartShell
  {engine}
  {legend}
  glucoseUnits={style.units}
  padding={plotPadding(compact, false)}
  showTimeAxis={false}
  {size}
>
  {#snippet tracks()}
    <ThresholdRules />
    <GlucoseTrack
      lineColorMode={style.lineColorMode}
      lineColor={style.lineColor}
      pointColorMode={style.pointColorMode}
      pointColor={style.pointColor}
      showPoints={style.showPoints}
      areaMode={style.areaMode}
      areaOpacity={style.areaOpacity}
      visibleOnly
    />
  {/snippet}
</GlucoseChartShell>
