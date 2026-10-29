<!--
  The dashboard glucose chart drawn from a view: the plot with every track and marker, then
  the legend. Everything it shows comes in as props; what a click opens is the host's.
-->
<script lang="ts">
  import type { Snippet } from "svelte";
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import ChartLegend from "./ChartLegend.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import SwimLaneTrack from "./tracks/SwimLaneTrack.svelte";
  import ThresholdRules from "./tracks/ThresholdRules.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import PredictionTrack from "./tracks/PredictionTrack.svelte";
  import IobCobTrack from "./tracks/IobCobTrack.svelte";
  import ChartHighlight from "./tracks/ChartHighlight.svelte";
  import DeviceEventMarkers from "./markers/DeviceEventMarkers.svelte";
  import SystemEventMarkers from "./markers/SystemEventMarkers.svelte";
  import TrackerMarkers from "./markers/TrackerMarkers.svelte";
  import BasalInjectionMarkers from "./markers/BasalInjectionMarkers.svelte";
  import BgCheckMarkers from "./markers/BgCheckMarkers.svelte";
  import { createLegendState } from "./legend-state.svelte.js";
  import { BasalDeliveryOrigin } from "./enums.js";
  import type { ChartInspector, LegendState } from "./context.svelte.js";
  import type { LegendToggle } from "./options.js";
  import type { ChartDataView } from "./view/chart-data-view.svelte.js";
  import type { PlotPadding } from "./view/plot-padding.js";
  import type { ChartFormat, GlucoseUnits } from "./format.js";
  import type { ChartLabels } from "./labels.js";
  import type { GlucoseLineAppearance, PredictionOptions } from "./options.js";

  interface Props {
    view: ChartDataView;
    glucoseUnits: GlucoseUnits;
    labels: ChartLabels;
    format: ChartFormat;
    /** The legend's toggles; the chart keeps its own when none is given. */
    legend?: LegendState;
    /** Legend toggles to leave out, for tracks the host has no data for. */
    omittedTracks?: readonly LegendToggle[];
    appearance?: GlucoseLineAppearance;
    /** Forecasts draw only when given and `show` is true. */
    predictions?: PredictionOptions;
    printing?: boolean;
    padding?: PlotPadding;
    /** A zoomed window inside the view's range. */
    brushDomain?: [Date, Date] | null;
    /** The plot box's height classes. */
    heightClass?: string;
    /** While false the plot's box is held empty, showing `placeholder`. */
    draw?: boolean;
    plotWidth?: number;
    plotHeight?: number;
    inspection?: ChartInspector;
    onMarkerClick?: (treatmentId: string) => void;
    onTimeClick?: (time: Date | undefined) => void;
    /** Shown in the plot's box until it is drawn. */
    placeholder?: Snippet;
    /** Between the plot and the legend, e.g. an overview strip. */
    belowPlot?: Snippet;
    /** Extra tooltip rows for the hovered time. */
    tooltipExtras?: Snippet<[{ time: Date }]>;
  }

  let {
    view,
    glucoseUnits,
    labels,
    format,
    legend: legendProp,
    omittedTracks = [],
    appearance = {},
    predictions,
    printing = false,
    padding,
    brushDomain = null,
    heightClass = "h-[400px] @md:h-[450px]",
    draw = true,
    plotWidth = $bindable(0),
    plotHeight = $bindable(0),
    inspection,
    onMarkerClick,
    onTimeClick,
    placeholder,
    belowPlot,
    tooltipExtras,
  }: Props = $props();

  const ownLegend = createLegendState();
  const legend = $derived(legendProp ?? ownLegend);

  const legendSystemEvents = $derived(
    view.displaySystemEvents.map((e) => ({ id: e.id, eventType: e.eventType, color: e.color })),
  );

  const hasUnreportedBasal = $derived(
    legend.basal &&
      (view.staleBasalData != null ||
        view.basalData.some((p) => p.origin === BasalDeliveryOrigin.Inferred)),
  );
</script>

<!-- The basal, mode and IOB/COB tracks take fixed shares of the height, so
     a 320px chart left them 38px, 13px and 58px tall on a phone: too little
     for their labels. 400px is what a portrait phone shows above the fold. -->
<div
  class={heightClass}
  data-testid="glucose-plot"
  bind:clientWidth={plotWidth}
  bind:clientHeight={plotHeight}
>
  {#if draw}
    <GlucoseChartShell
      {view}
      {inspection}
      {legend}
      {glucoseUnits}
      {labels}
      {format}
      {printing}
      {brushDomain}
      {padding}
    >
      {#snippet tracks()}
        <BasalTrack />
        <SwimLaneTrack />
        <ThresholdRules />
        <GlucoseTrack {...appearance} />
        {#if predictions?.show}
          <PredictionTrack displayMode={predictions.displayMode} enabled={predictions.enabled} />
        {/if}
        <IobCobTrack {onMarkerClick} />
        <DeviceEventMarkers {onMarkerClick} />
        <SystemEventMarkers />
        <TrackerMarkers />
        <BasalInjectionMarkers />
        <BgCheckMarkers />
        <ChartHighlight />
      {/snippet}
      {#snippet overlays()}
        <ChartTooltip {onTimeClick} {tooltipExtras} />
      {/snippet}
    </GlucoseChartShell>
  {:else}
    {@render placeholder?.()}
  {/if}
</div>

{@render belowPlot?.()}

<ChartLegend
  omitted={omittedTracks}
  {glucoseUnits}
  {labels}
  {printing}
  glucoseData={view.glucoseData}
  highThreshold={view.thresholds.high}
  lowThreshold={view.thresholds.low}
  veryHighThreshold={view.thresholds.veryHigh}
  veryLowThreshold={view.thresholds.veryLow}
  showBasal={legend.basal}
  showIob={legend.iob}
  showCob={legend.cob}
  showBolus={legend.bolus}
  showCarbs={legend.carbs}
  showPumpModes={legend.pumpModes}
  showAlarms={legend.alarms}
  showScheduledTrackers={legend.scheduledTrackers}
  showOverrideSpans={legend.overrideSpans}
  showProfileSpans={legend.profileSpans}
  showActivitySpans={legend.activitySpans}
  onToggleBasal={() => legend.toggle("basal")}
  onToggleIob={() => legend.toggle("iob")}
  onToggleCob={() => legend.toggle("cob")}
  onToggleBolus={() => legend.toggle("bolus")}
  onToggleCarbs={() => legend.toggle("carbs")}
  onTogglePumpModes={() => legend.toggle("pumpModes")}
  onToggleAlarms={() => legend.toggle("alarms")}
  onToggleScheduledTrackers={() => legend.toggle("scheduledTrackers")}
  onToggleOverrideSpans={() => legend.toggle("overrideSpans")}
  onToggleProfileSpans={() => legend.toggle("profileSpans")}
  onToggleActivitySpans={() => legend.toggle("activitySpans")}
  deviceEventMarkers={view.deviceEventMarkers}
  systemEvents={legendSystemEvents}
  pumpModeSpans={view.displayPumpModeSpans}
  scheduledTrackerMarkers={view.displayTrackerMarkers}
  currentPumpMode={view.currentPumpMode}
  uniquePumpModes={view.uniquePumpModes}
  expandedPumpModes={legend.expandedPumpModes}
  hasBgChecks={view.bgCheckMarkers.length > 0}
  {hasUnreportedBasal}
  hasScheduledBasal={legend.basal && view.scheduledBasalData.length > 0}
  targetLow={view.thresholds.targetLow}
  targetHigh={view.thresholds.targetHigh}
  onToggleExpandedPumpModes={() => legend.toggle("expandedPumpModes")}
/>
