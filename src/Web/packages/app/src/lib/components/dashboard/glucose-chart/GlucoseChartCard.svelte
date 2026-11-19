<script lang="ts">
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import { findNearbyEntries } from "./engine/nearby-entries";
  import type { EntryRecord } from "#lib/constants/entry-categories.js";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { IsMobile } from "#lib/hooks/is-mobile.svelte.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import {
    predictionEnabled,
    predictionDisplayMode,
    glucoseChartLookback,
    chartLineColorMode,
    chartLineColor,
    chartPointColorMode,
    chartPointColor,
    chartShowPoints,
    chartAreaMode,
    chartAreaOpacity,
    colorTheme,
    glucoseUnits as glucoseUnitsPreference,
  } from "#lib/stores/appearance-store.svelte.js";
  import type { PredictionDisplayMode } from "#lib/stores/appearance-store.svelte.js";
  import { BasalDeliveryOrigin, type SystemEventType } from "#lib/api/index.js";
  import PredictionSettings from "../PredictionSettings.svelte";
  import MiniOverviewChart from "../MiniOverviewChart.svelte";
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import ChartLegend from "./ChartLegend.svelte";
  import ZoomIndicator from "./ZoomIndicator.svelte";
  import { createChartDataEngine } from "./engine/chart-data-engine.svelte";
  import { createPointInspection, syncInspectionDialogs } from "./engine/point-inspection.svelte";
  import type { LegendState } from "./chart-context.svelte";
  import type { TransformedChartData } from "#lib/utils/chart-data-transform.js";
  import type { PredictionData } from "#lib/api/predictions.remote.js";
  import { PrintMode } from "#lib/components/charts/print/print-mode.svelte.js";

  // Tracks
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import SwimLaneTrack from "./tracks/SwimLaneTrack.svelte";
  import ThresholdRules from "./tracks/ThresholdRules.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import PredictionTrack from "./tracks/PredictionTrack.svelte";
  import IobCobTrack from "./tracks/IobCobTrack.svelte";
  import DeviceEventMarkers from "./markers/DeviceEventMarkers.svelte";
  import SystemEventMarkers from "./markers/SystemEventMarkers.svelte";
  import TrackerMarkers from "./markers/TrackerMarkers.svelte";
  import ChartHighlight from "./tracks/ChartHighlight.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";
  import { openDayInReview } from "./day-in-review";

  import ChartEntryActions from "./ChartEntryActions.svelte";
  import { canLogEvents } from "#lib/components/entries/can-log-events.js";
  import BasalInjectionMarkers from "./markers/BasalInjectionMarkers.svelte";
  import BgCheckMarkers from "./markers/BgCheckMarkers.svelte";
  import type { GlucoseUnits } from "#lib/stores/appearance-store.svelte.js";
  import { afterNextPaint } from "#lib/utils/after-next-paint.js";
  import { plotPadding } from "./plot-padding";
  import { writePlotSize } from "./plot-size";
  import { forecastDotRadius, nightscoutTicks, sgvDotRadius } from "./engine/nightscout-sizing";

  interface Props {
    dateRange?: { from: Date | string; to: Date | string };
    initialChartData?: TransformedChartData | null;
    initialWindowStart?: number;
    streamedHistoricalData?: Promise<TransformedChartData | null>;
    externalPredictionData?: PredictionData | null;
    showPredictions?: boolean;
    /** Whether forecasts can be made, as the server found when it rendered the page. */
    initialPredictionsAvailable?: boolean | null;
    defaultFocusHours?: number;
    heightClass?: string;
    demoMode?: boolean;
    /**
     * While true the main plot is left undrawn in its reserved space, and it is drawn a frame
     * after this turns false. The dashboard holds it until the realtime store's current reading
     * has landed: the plot is the costliest thing on the page to draw, so drawn in the same
     * task it delays the current reading's paint behind it.
     */
    deferDrawing?: boolean;
    glucoseUnits?: GlucoseUnits;
    /**
     * The server's rendering of the plot (`#lib/server/plot-snapshot.js`), shown until the live plot
     * is drawn.
     */
    plotSnapshot?: string | null;
    /** Keep the plot's size where the server reads it, to render the next document's snapshot. */
    recordsPlotSize?: boolean;
  }

  let {
    dateRange,
    initialChartData,
    initialWindowStart,
    streamedHistoricalData,
    externalPredictionData,
    showPredictions = true,
    initialPredictionsAvailable,
    defaultFocusHours,
    heightClass,
    demoMode,
    deferDrawing = false,
    glucoseUnits,
    plotSnapshot,
    recordsPlotSize = false,
  }: Props = $props();

  const realtimeStore = getRealtimeStore();

  // svelte-ignore state_referenced_locally
  let plotDrawn = $state(!deferDrawing);
  $effect(() => {
    if (deferDrawing || plotDrawn) return;
    // Whatever ended the deferral paints before the plot is drawn.
    return afterNextPaint(() => (plotDrawn = true));
  });
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  let plotWidth = $state(0);
  let plotHeight = $state(0);

  // Narrower than 672px (@2xl), the card drops its chrome (container queries, so the chart is
  // never remounted) and the axis gutters narrow, so the chart can use the full width. Until
  // the card is measured, the viewport stands in for its width.
  let cardWidth = $state(0);
  const isMobile = new IsMobile();
  const compact = $derived(cardWidth > 0 ? cardWidth < 672 : isMobile.current);
  const print = new PrintMode();

  // The Nightscout theme draws cgm-remote-monitor's focus and context charts: no card chrome,
  // no legend, ticks inside the right edge. Paper keeps the default chart.
  const nightscout = $derived(colorTheme.current === "nightscout" && !print.active);

  const chartPadding = $derived(
    nightscout
      ? { left: 0, right: 0, top: 0, bottom: 30 }
      : plotPadding(compact, print.active)
  );

  const nightscoutFocusMs = $derived.by(() => {
    const from = brushDomain?.[0] ?? engine.displayDateRange.from;
    const to = brushDomain?.[1] ?? engine.displayDateRange.to;
    return new Date(to).getTime() - new Date(from).getTime();
  });

  $effect(() => {
    if (!recordsPlotSize || print.active || plotWidth === 0 || plotHeight === 0 || cardWidth === 0) return;
    writePlotSize({ width: plotWidth, height: plotHeight, compact });
  });

  // ===== ENGINE =====
  const engine = createChartDataEngine({
    get dateRange() { return dateRange; },
    get focusHours() { return defaultFocusHours; },
    get initialChartData() { return initialChartData; },
    get initialWindowStart() { return initialWindowStart; },
    get streamedHistoricalData() { return streamedHistoricalData; },
    get externalPredictionData() { return externalPredictionData; },
    get enablePredictions() { return showPredictions; },
    get initialPredictionsAvailable() { return initialPredictionsAvailable; },
    get demoMode() { return demoMode; },
  });

  // ===== POINT INSPECTION =====
  const inspection = createPointInspection(
    engine.finders,
    () => engine.glucoseData,
    {
      iobData: () => engine.iobData,
      cobData: () => engine.cobData,
      basalData: () => engine.basalData,
    },
    () => glucoseUnits,
    canLogEvents,
  );

  // ===== LEGEND STATE =====
  let showIob = $state(true);
  let showCob = $state(true);
  let showBasal = $state(true);
  let showBolus = $state(true);
  let showCarbs = $state(true);
  let showDeviceEvents = $state(true);
  let showAlarms = $state(true);
  let showScheduledTrackers = $state(true);
  let showOverrideSpans = $state(false);
  let showProfileSpans = $state(false);
  let showActivitySpans = $state(false);
  let showBasalInjections = $state(true);
  let showPumpModes = $state(true);
  let expandedPumpModes = $state(false);

  const legend: LegendState = {
    get iob() { return showIob; },
    get cob() { return showCob; },
    get basal() { return showBasal; },
    get bolus() { return showBolus; },
    get carbs() { return showCarbs; },
    get deviceEvents() { return showDeviceEvents; },
    get alarms() { return showAlarms; },
    get scheduledTrackers() { return showScheduledTrackers; },
    get basalInjections() { return showBasalInjections; },
    get overrideSpans() { return showOverrideSpans; },
    get profileSpans() { return showProfileSpans; },
    get activitySpans() { return showActivitySpans; },
    get pumpModes() { return showPumpModes; },
    get expandedPumpModes() { return expandedPumpModes; },
    toggle(key: string) {
      switch (key) {
        case "iob": showIob = !showIob; break;
        case "cob": showCob = !showCob; break;
        case "basal": showBasal = !showBasal; break;
        case "bolus": showBolus = !showBolus; break;
        case "carbs": showCarbs = !showCarbs; break;
        case "deviceEvents": showDeviceEvents = !showDeviceEvents; break;
        case "alarms": showAlarms = !showAlarms; break;
        case "scheduledTrackers": showScheduledTrackers = !showScheduledTrackers; break;
        case "basalInjections": showBasalInjections = !showBasalInjections; break;
        case "overrideSpans": showOverrideSpans = !showOverrideSpans; break;
        case "profileSpans": showProfileSpans = !showProfileSpans; break;
        case "activitySpans": showActivitySpans = !showActivitySpans; break;
        case "pumpModes":
          showPumpModes = !showPumpModes;
          if (!showPumpModes) expandedPumpModes = false;
          break;
      }
    },
  };

  // ===== BRUSH / ZOOM =====
  let brushDomain = $state<[Date, Date] | null>(null);
  const isZoomed = $derived(brushDomain !== null);

  function resetZoom() {
    brushDomain = null;
  }

  function handleMiniChartBrush(domain: [Date, Date] | null) {
    if (domain) {
      const now = Date.now();
      const selectionEnd = Math.min(domain[1].getTime(), now);
      const spanMs = selectionEnd - domain[0].getTime();
      const spanHours = spanMs / (60 * 60 * 1000);
      const roundedSpan = Math.round(spanHours * 2) / 2;
      const clampedSpan = Math.max(1, Math.min(48, roundedSpan));
      glucoseChartLookback.current = clampedSpan;
      brushDomain = domain;
    } else {
      brushDomain = null;
    }
  }

  // ===== PREDICTIONS =====
  // A printed report is a record of what happened; a forecast made at print
  // time is not part of it.
  const effectiveShowPredictions = $derived(
    showPredictions && engine.effectiveShowPredictions && !print.active,
  );

  // The controls' line is held until the prediction status says nothing will fill it.
  const reservePredictionControls = $derived(
    showPredictions && engine.predictionAvailability !== "unavailable",
  );

  let predictionModeValue = $state(predictionDisplayMode.current);

  function handlePredictionModeChange(value: PredictionDisplayMode) {
    if (value && value !== predictionModeValue) {
      predictionModeValue = value;
      predictionDisplayMode.current = value;
    }
  }

  // ===== ENTRY EDIT / MARKER CLICK =====
  let entryActions = $state<ReturnType<typeof ChartEntryActions>>();

  function findAllNearbyEntries(time: Date): EntryRecord[] {
    return findNearbyEntries(
      [...engine.bolusMarkers, ...engine.carbMarkers, ...engine.deviceEventMarkers],
      time,
      (id) => realtimeStore.findEntryByTreatmentId(id)
    );
  }

  function handleMarkerClick(treatmentId: string) {
    void entryActions?.editTreatment(treatmentId);
  }

  function closeAllInspections() {
    inspection.close();
  }

  // ===== MINI OVERVIEW DATA =====
  const miniPredictionData = $derived.by(() => {
    if (!effectiveShowPredictions || !engine.predictionData?.curves?.main) {
      return null;
    }
    return engine.predictionData.curves.main.map((p) => ({
      time: new Date(p.timestamp),
      value: p.value,
    }));
  });

  const miniSelectedDomain = $derived<[Date, Date]>(
    brushDomain ?? [
      engine.displayDateRangeWithPredictions.from,
      engine.displayDateRangeWithPredictions.to,
    ],
  );

  const legendSystemEvents = $derived(
    engine.displaySystemEvents.map(
      (e): { id?: string; eventType?: SystemEventType; color?: string } => ({
        id: e.id,
        eventType: e.eventType,
        color: e.color,
      }),
    ),
  );

  const glucoseInspectionDialog = lazyComponent(
    () => import("./dialogs/GlucoseInspectionDialog.svelte"),
  );
  const deliveryInspectionDialog = lazyComponent(
    () => import("./dialogs/DeliveryInspectionDialog.svelte"),
  );
  const treatmentInspectionDialog = lazyComponent(
    () => import("./dialogs/TreatmentInspectionDialog.svelte"),
  );
  syncInspectionDialogs(() => inspection, {
    glucose: glucoseInspectionDialog,
    delivery: deliveryInspectionDialog,
    treatment: treatmentInspectionDialog,
  });
</script>

{#snippet chartBody()}
  {#if !nightscout}
    <CardHeader class="pb-2 px-1 @2xl/chart-card:px-6">
      <div class="flex items-center justify-between flex-wrap gap-2">
        <CardTitle class="flex items-center gap-2">
          Blood glucose
          {#if displayDemoMode}
            <Badge variant="demo">
              Demo
            </Badge>
        {/if}
      </CardTitle>

      <!-- Until the prediction status says there is nothing to show, this slot holds the
           controls' space whether or not they render, so the plot does not move when the status
           arrives: min-h-8 is the segmented xs toggle's height (h-7 items in a p-0.5 track), and
           below @lg the controls do not fit beside the title, so the slot takes its own line. -->
      <div
        data-testid="prediction-controls"
        class="flex items-center gap-2 print:hidden {reservePredictionControls
          ? 'min-h-8 basis-full @lg:basis-auto'
          : ''}"
      >
        <PredictionSettings
          showPredictions={effectiveShowPredictions}
          predictionMode={predictionModeValue}
          onPredictionModeChange={handlePredictionModeChange}
        />
      </div>
    </div>
  </CardHeader>
  {/if}

  <!-- The bare plot reaches into the page's gutter only while <main> is phone-width, where
       pages pad p-3: a narrow chart on a wide page, e.g. a clock builder preview, stays in
       its box. -->
  <CardContent
    class={nightscout ? "p-0" : "p-0 @max-md/main:-mx-2 @2xl/chart-card:p-2"}
  >
    <ZoomIndicator {isZoomed} brushXDomain={brushDomain} onResetZoom={resetZoom} />

    <!-- The basal, mode and IOB/COB tracks take fixed shares of the height, so
         a 320px chart left them 38px, 13px and 58px tall on a phone: too little
         for their labels. 400px is what a portrait phone shows above the fold. -->
    <div
      class={heightClass ?? (nightscout ? "h-[420px] @md:h-[480px]" : "h-[400px] @md:h-[450px]")}
      data-testid="glucose-plot"
      bind:clientWidth={plotWidth}
      bind:clientHeight={plotHeight}
    >
      {#if plotDrawn}
        <GlucoseChartShell
          {engine}
          {inspection}
          {legend}
          {glucoseUnits}
          brushDomain={brushDomain}
          padding={chartPadding}
          layoutMode={nightscout ? "nightscout" : "default"}
          timeAxisClass={nightscout ? "fill-foreground" : undefined}
        >
          {#snippet tracks()}
            {#if nightscout}
              <BasalTrack variant="nightscout" />
              <ThresholdRules variant="nightscout" />
              <GlucoseTrack
                showLine={false}
                showPoints={true}
                lineColorMode="single"
                pointColorMode="threshold"
                pointRadius={sgvDotRadius(plotWidth, nightscoutFocusMs)}
                pointClass=""
                axisPlacement="inside-right"
                axisTicks={nightscoutTicks(glucoseUnits ?? glucoseUnitsPreference.current, engine.thresholds)}
              />
              {#if effectiveShowPredictions}
                <!-- Nightscout has no cone: that mode draws the main forecast's dots. -->
                <PredictionTrack
                  displayMode={predictionDisplayMode.current === "cone" ? "main" : undefined}
                  markStyle="dots"
                  dotRadius={forecastDotRadius(plotWidth, nightscoutFocusMs)}
                />
              {/if}
              <IobCobTrack variant="nightscout" onMarkerClick={handleMarkerClick} />
              <DeviceEventMarkers onMarkerClick={handleMarkerClick} />
              <BgCheckMarkers />
              <ChartHighlight />
            {:else}
              <BasalTrack />
              <SwimLaneTrack />
              <ThresholdRules />
              <GlucoseTrack
                lineColorMode={chartLineColorMode.current}
                lineColor={chartLineColor.current}
                pointColorMode={chartPointColorMode.current}
                pointColor={chartPointColor.current}
                showPoints={chartShowPoints.current}
                areaMode={chartAreaMode.current}
                areaOpacity={chartAreaOpacity.current}
              />
              {#if effectiveShowPredictions}
                <PredictionTrack />
              {/if}
              <IobCobTrack onMarkerClick={handleMarkerClick} />
              <DeviceEventMarkers onMarkerClick={handleMarkerClick} />
              <SystemEventMarkers />
              <TrackerMarkers />
              <BasalInjectionMarkers />
              <BgCheckMarkers />
              <ChartHighlight />
            {/if}
          {/snippet}
          {#snippet overlays()}
            <ChartTooltip onTimeClick={openDayInReview} />
          {/snippet}
        </GlucoseChartShell>
      {:else if plotSnapshot}
        <div class="h-full w-full overflow-hidden" data-testid="plot-snapshot" aria-hidden="true">
          <!-- eslint-disable-next-line svelte/no-at-html-tags -- the server's own render of this chart's components, no user input -->
          {@html plotSnapshot}
        </div>
      {/if}
    </div>

    {#if engine.glucoseData.length > 0}
      <MiniOverviewChart
        variant={nightscout ? "nightscout" : "default"}
        data={engine.glucoseData}
        fullXDomain={[engine.fullXDomain.from, engine.fullXDomain.to]}
        selectedXDomain={miniSelectedDomain}
        yDomain={[0, engine.thresholds.glucoseYMax]}
        expanded={true}
        onSelectionChange={(domain) => handleMiniChartBrush(domain)}
        predictionData={miniPredictionData}
        showPredictions={effectiveShowPredictions && predictionEnabled.current}
        {isZoomed}
        thresholds={engine.thresholds}
        lineColorMode={chartLineColorMode.current}
        lineColor={chartLineColor.current}
        areaMode={chartAreaMode.current}
        areaOpacity={chartAreaOpacity.current}
        padding={{
          left: chartPadding.left,
          right: chartPadding.right,
          top: nightscout ? 4 : 20,
          bottom: nightscout ? 30 : 20,
        }}
      />
    {/if}

    {#if !nightscout}
    <ChartLegend
        {glucoseUnits}
        glucoseData={engine.glucoseData}
        highThreshold={engine.thresholds.high}
        lowThreshold={engine.thresholds.low}
        veryHighThreshold={engine.thresholds.veryHigh}
        veryLowThreshold={engine.thresholds.veryLow}
        {showBasal}
        {showIob}
        {showCob}
        {showBolus}
        {showCarbs}
        {showPumpModes}
        {showAlarms}
        {showScheduledTrackers}
        {showOverrideSpans}
        {showProfileSpans}
        {showActivitySpans}
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
        deviceEventMarkers={engine.deviceEventMarkers}
        systemEvents={legendSystemEvents}
        pumpModeSpans={engine.displayPumpModeSpans}
        scheduledTrackerMarkers={engine.displayTrackerMarkers}
        currentPumpMode={engine.currentPumpMode}
        uniquePumpModes={engine.uniquePumpModes}
        {expandedPumpModes}
        hasBgChecks={engine.bgCheckMarkers.length > 0}
        hasUnreportedBasal={showBasal &&
          (engine.staleBasalData != null ||
            engine.basalData.some((p) => p.origin === BasalDeliveryOrigin.Inferred))}
        hasScheduledBasal={showBasal && engine.scheduledBasalData.length > 0}
        targetLow={engine.thresholds.targetLow}
        targetHigh={engine.thresholds.targetHigh}
        onToggleExpandedPumpModes={() => (expandedPumpModes = !expandedPumpModes)}
      />
    {/if}
  </CardContent>
{/snippet}

<div class="@container/chart-card" bind:clientWidth={cardWidth}>
  {#if nightscout}
    <div class="@container bg-(--ns-chart-bg)" data-testid="nightscout-chart">
      {@render chartBody()}
    </div>
  {:else}
    <Card bareWhenNarrow class="@container">
      {@render chartBody()}
    </Card>
  {/if}
</div>

<!-- The picker step of point inspection, marker disambiguation, and the entry editor. -->
<ChartEntryActions bind:this={entryActions} {inspection} findNearby={findAllNearbyEntries} />

{#if inspection.timestamp && inspection.glucosePoint && inspection.context}
  {#if glucoseInspectionDialog.component}
    <glucoseInspectionDialog.component
      {glucoseUnits}
      bind:open={glucoseInspectionDialog.open}
      timestamp={inspection.timestamp}
      glucoseValue={inspection.glucosePoint.sgv}
      glucoseColor={inspection.glucosePoint.color}
      previousGlucoseValue={inspection.context.previousGlucoseValue}
      dataSource={inspection.context.dataSource}
      glucoseData={engine.glucoseData}
      highThreshold={engine.thresholds.high}
      lowThreshold={engine.thresholds.low}
      iob={inspection.context.iob}
      cob={inspection.context.cob}
      basalRate={inspection.context.basalRate}
      scheduledBasalRate={inspection.context.scheduledBasalRate}
      basalOrigin={inspection.context.basalOrigin}
      pumpMode={inspection.context.pumpMode}
      overrideState={inspection.context.overrideState}
      profileName={inspection.context.profileName}
      activityStates={inspection.context.activityStates}
      hasDeliveryContext={inspection.context.basalRate != null}
      hasTreatmentContext={inspection.context.nearbyBolus != null ||
        inspection.context.nearbyCarbs != null}
      onClose={closeAllInspections}
      onNavigateDelivery={() => inspection.navigateTo("delivery")}
      onNavigateTreatment={() => inspection.navigateTo("treatment")}
    />
  {/if}

  {#if deliveryInspectionDialog.component}
    <deliveryInspectionDialog.component
      {glucoseUnits}
      bind:open={deliveryInspectionDialog.open}
      timestamp={inspection.timestamp}
      basalRate={inspection.context.basalRate}
      scheduledBasalRate={inspection.context.scheduledBasalRate}
      basalOrigin={inspection.context.basalOrigin}
      pumpMode={inspection.context.pumpMode}
      overrideState={inspection.context.overrideState}
      profileName={inspection.context.profileName}
      activityStates={inspection.context.activityStates}
      iob={inspection.context.iob}
      isStaleBasal={inspection.context.isStaleBasal}
      dataSource={inspection.context.dataSource}
      glucoseData={engine.glucoseData}
      highThreshold={engine.thresholds.high}
      lowThreshold={engine.thresholds.low}
      hasGlucoseContext={true}
      hasTreatmentContext={inspection.context.nearbyBolus != null ||
        inspection.context.nearbyCarbs != null}
      onClose={closeAllInspections}
      onNavigateGlucose={() => inspection.navigateTo("glucose")}
      onNavigateTreatment={() => inspection.navigateTo("treatment")}
    />
  {/if}

  {#if treatmentInspectionDialog.component}
    <treatmentInspectionDialog.component
      {glucoseUnits}
      bind:open={treatmentInspectionDialog.open}
      timestamp={inspection.timestamp}
      bolusInsulin={inspection.context.nearbyBolus?.insulin}
      bolusType={inspection.context.nearbyBolus?.bolusType}
      bolusDataSource={inspection.context.nearbyBolus?.dataSource}
      carbGrams={inspection.context.nearbyCarbs?.carbs}
      carbLabel={inspection.context.nearbyCarbs?.label}
      carbMealPeriod={inspection.context.nearbyCarbs?.mealPeriod}
      carbDataSource={inspection.context.nearbyCarbs?.dataSource}
      iob={inspection.context.iob}
      cob={inspection.context.cob}
      glucoseValue={inspection.glucosePoint.sgv}
      glucoseData={engine.glucoseData}
      highThreshold={engine.thresholds.high}
      lowThreshold={engine.thresholds.low}
      hasGlucoseContext={true}
      hasDeliveryContext={inspection.context.basalRate != null}
      onClose={closeAllInspections}
      onNavigateGlucose={() => inspection.navigateTo("glucose")}
      onNavigateDelivery={() => inspection.navigateTo("delivery")}
      onEditEntry={() => {
        const treatmentId =
          inspection.context?.nearbyBolus?.treatmentId ??
          inspection.context?.nearbyCarbs?.treatmentId;
        closeAllInspections();
        if (treatmentId) handleMarkerClick(treatmentId);
      }}
    />
  {/if}
{/if}
