<script lang="ts">
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import { findNearbyEntries } from "./engine/nearby-entries";
  import type { Snippet } from "svelte";
  import type { TransformedChartData } from "#lib/utils/chart-data-transform.js";
  import type { PredictionData } from "#lib/api/predictions.remote.js";
  import type { GlucoseChartContext, LegendState } from "./chart-context.svelte";
  import type { EntryRecord } from "#lib/constants/entry-categories.js";
  import {
    createChartDataEngine,
  } from "./engine/chart-data-engine.svelte";
  import { createPointInspection, syncInspectionDialogs } from "./engine/point-inspection.svelte";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import {
    chartLineColorMode,
    chartLineColor,
    chartPointColorMode,
    chartPointColor,
    chartShowPoints,
    chartAreaMode,
    chartAreaOpacity,
  } from "#lib/stores/appearance-store.svelte.js";

  // Shell & tracks
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import BasalTrack from "./tracks/BasalTrack.svelte";
  import SwimLaneTrack from "./tracks/SwimLaneTrack.svelte";
  import ThresholdRules from "./tracks/ThresholdRules.svelte";
  import GlucoseTrack from "./tracks/GlucoseTrack.svelte";
  import PredictionTrack from "./tracks/PredictionTrack.svelte";
  import IobCobTrack from "./tracks/IobCobTrack.svelte";
  import DeviceEventMarkers from "./markers/DeviceEventMarkers.svelte";
  import SystemEventMarkers from "./markers/SystemEventMarkers.svelte";
  import TrackerMarkers from "./markers/TrackerMarkers.svelte";
  import BgCheckMarkers from "./markers/BgCheckMarkers.svelte";
  import ChartHighlight from "./tracks/ChartHighlight.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";
  import { openDayInReview } from "./day-in-review";

  import ChartEntryActions from "./ChartEntryActions.svelte";
  import { canLogEvents } from "#lib/components/entries/can-log-events.js";

  interface Props {
    dateRange?: { from: Date | string; to: Date | string };
    focusHours?: number;
    initialChartData?: TransformedChartData | null;
    initialWindowStart?: number;
    streamedHistoricalData?: Promise<TransformedChartData | null>;
    externalPredictionData?: PredictionData | null;
    enablePredictions?: boolean;
    demoMode?: boolean;
    heightClass?: string;
    enableInspection?: boolean;
    legend?: LegendState;
    brushDomain?: [Date, Date] | null;
    selectionDomain?: [Date, Date] | null;
    onSelectionChange?: (domain: [Date, Date] | null) => void;
    annotations?: Snippet<[GlucoseChartContext]>;
    tooltipExtras?: Snippet<[{ time: Date }]>;
  }

  let {
    dateRange,
    focusHours,
    initialChartData,
    initialWindowStart,
    streamedHistoricalData,
    externalPredictionData,
    enablePredictions,
    demoMode,
    heightClass = "h-full",
    enableInspection = true,
    legend,
    brushDomain,
    selectionDomain,
    onSelectionChange,
    annotations,
    tooltipExtras,
  }: Props = $props();

  // ---- Engine ----
  const engine = createChartDataEngine({
    get dateRange() { return dateRange; },
    get focusHours() { return focusHours; },
    get initialChartData() { return initialChartData; },
    get initialWindowStart() { return initialWindowStart; },
    get streamedHistoricalData() { return streamedHistoricalData; },
    get externalPredictionData() { return externalPredictionData; },
    get enablePredictions() { return enablePredictions; },
    get demoMode() { return demoMode; },
  });

  // ---- Point inspection ----
  // svelte-ignore state_referenced_locally
  const inspection = enableInspection
    ? createPointInspection(
        engine.finders,
        () => engine.glucoseData,
        {
          iobData: () => engine.iobData,
          cobData: () => engine.cobData,
          basalData: () => engine.basalData,
        },
        undefined,
        canLogEvents,
      )
    : undefined;

  // ---- Realtime store (for entry lookups) ----
  const realtimeStore = getRealtimeStore();

  // ---- Entry edit ----
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
    inspection?.close();
  }

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

<GlucoseChartShell
  {engine}
  {inspection}
  {legend}
  {brushDomain}
  {heightClass}
  {selectionDomain}
  {onSelectionChange}
>
  {#snippet tracks(ctx)}
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
    <BgCheckMarkers />
    {#if enablePredictions !== false}
      <PredictionTrack />
    {/if}
    <IobCobTrack
      onMarkerClick={enableInspection ? handleMarkerClick : undefined}
    />
    <DeviceEventMarkers
      onMarkerClick={enableInspection ? handleMarkerClick : undefined}
    />
    <SystemEventMarkers />
    <TrackerMarkers />
    {@render annotations?.(ctx)}
    <ChartHighlight />
  {/snippet}
  {#snippet overlays()}
    <ChartTooltip {tooltipExtras} onTimeClick={openDayInReview} />
  {/snippet}
</GlucoseChartShell>

<!-- The picker step of point inspection, marker disambiguation, and the entry editor. -->
{#if enableInspection}
  <ChartEntryActions bind:this={entryActions} {inspection} findNearby={findAllNearbyEntries} />
{/if}

{#if inspection}
  {#if inspection.timestamp && inspection.glucosePoint && inspection.context}
    {#if glucoseInspectionDialog.component}
      <glucoseInspectionDialog.component
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
{/if}
