<script lang="ts">
  import { lazyComponent } from "$lib/utils/lazy-component.svelte";
  import { findNearbyEntries } from "./engine/nearby-entries";
  import type { Snippet } from "svelte";
  import type { TransformedChartData } from "$lib/utils/chart-data-transform";
  import type { PredictionData } from "$api/predictions.remote";
  import type { GlucoseChartContext, LegendState } from "./chart-context.svelte";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    createChartDataEngine,
  } from "./engine/chart-data-engine.svelte";
  import { createPointInspection, syncInspectionDialogs } from "./engine/point-inspection.svelte";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import {
    chartLineColorMode,
    chartLineColor,
    chartPointColorMode,
    chartPointColor,
    chartShowPoints,
    chartAreaMode,
    chartAreaOpacity,
  } from "$lib/stores/appearance-store.svelte";
  import { getEntryByTreatmentId } from "$api/entries.remote";

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

  import PointInspectionPicker from "./dialogs/PointInspectionPicker.svelte";

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
    ? createPointInspection(engine.finders, () => engine.glucoseData, {
        iobData: () => engine.iobData,
        cobData: () => engine.cobData,
        basalData: () => engine.basalData,
      })
    : undefined;

  // ---- Realtime store (for entry lookups) ----
  const realtimeStore = getRealtimeStore();

  // ---- Entry edit state ----
  let selectedEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let nearbyEntries = $state<EntryRecord[]>([]);

  // ---- Inspection dialog state ----
  let isPickerOpen = $derived(inspection?.activeDialog === "picker");

  // ---- Entry lookup helpers ----
  function findAllNearbyEntries(time: Date): EntryRecord[] {
    return findNearbyEntries(
      [...engine.bolusMarkers, ...engine.carbMarkers, ...engine.deviceEventMarkers],
      time,
      (id) => realtimeStore.findEntryByTreatmentId(id)
    );
  }

  async function handleMarkerClick(treatmentId: string) {
    let entry: EntryRecord | null =
      realtimeStore.findEntryByTreatmentId(treatmentId) ?? null;

    if (!entry) {
      const result = await getEntryByTreatmentId({ treatmentId }).run();
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- generated entry shape bridged to the app EntryRecord union
      entry = result as EntryRecord | null;
    }

    if (!entry) {
      console.warn(
        `[GlucoseChart] No entry found for treatmentId: ${treatmentId}`,
      );
      return;
    }

    const time = new Date(entry.data.mills ?? 0);
    const nearby = findAllNearbyEntries(time);

    if (nearby.length <= 1) {
      selectedEntry = entry;
      correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
      entryEditDialog.open = true;
    } else {
      nearbyEntries = nearby;
      disambiguationDialog.open = true;
    }
  }

  function selectEntryFromList(entry: EntryRecord) {
    disambiguationDialog.open = false;
    nearbyEntries = [];
    selectedEntry = entry;
    correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
    entryEditDialog.open = true;
  }

  // ---- Inspection dialog handlers ----
  function handleInspectionSelect(type: "glucose" | "delivery" | "treatment") {
    inspection?.selectDialog(type);
  }

  function closeAllInspections() {
    inspection?.close();
  }

  const entryEditDialog = lazyComponent(
    () => import("$lib/components/entries/EntryEditDialog.svelte"),
  );
  const disambiguationDialog = lazyComponent(
    () => import("./dialogs/TreatmentDisambiguationDialog.svelte"),
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

<!-- Entry Edit Dialog -->
{#if entryEditDialog.component}
  <entryEditDialog.component
    bind:open={entryEditDialog.open}
    entry={selectedEntry}
    {correlatedRecords}
    onClose={() => {
      entryEditDialog.open = false;
      selectedEntry = null;
      correlatedRecords = [];
    }}
  />
{/if}

<!-- Disambiguation Dialog -->
{#if disambiguationDialog.component}
  <disambiguationDialog.component
    bind:open={disambiguationDialog.open}
    entries={nearbyEntries}
    onSelect={selectEntryFromList}
    onClose={() => {
      disambiguationDialog.open = false;
      nearbyEntries = [];
    }}
  />
{/if}

<!-- Point Inspection Dialogs -->
{#if inspection}
  <PointInspectionPicker
    bind:open={isPickerOpen}
    options={inspection.pickerOptions}
    onSelect={handleInspectionSelect}
    onClose={closeAllInspections}
  />

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
        highThreshold={engine.highThreshold}
        lowThreshold={engine.lowThreshold}
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
        highThreshold={engine.highThreshold}
        lowThreshold={engine.lowThreshold}
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
        carbDataSource={inspection.context.nearbyCarbs?.dataSource}
        iob={inspection.context.iob}
        cob={inspection.context.cob}
        glucoseValue={inspection.glucosePoint.sgv}
        glucoseData={engine.glucoseData}
        highThreshold={engine.highThreshold}
        lowThreshold={engine.lowThreshold}
        hasGlucoseContext={true}
        hasDeliveryContext={inspection.context.basalRate != null}
        onClose={closeAllInspections}
        onNavigateGlucose={() => inspection.navigateTo("glucose")}
        onNavigateDelivery={() => inspection.navigateTo("delivery")}
        onEditEntry={() => {
          closeAllInspections();
          if (inspection.context?.nearbyBolus?.treatmentId) {
            handleMarkerClick(inspection.context.nearbyBolus.treatmentId);
          } else if (inspection.context?.nearbyCarbs?.treatmentId) {
            handleMarkerClick(inspection.context.nearbyCarbs.treatmentId);
          }
        }}
      />
    {/if}
  {/if}
{/if}
