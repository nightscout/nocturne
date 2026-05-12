<script lang="ts">
  import type { Snippet } from "svelte";
  import type { TransformedChartData } from "$lib/utils/chart-data-transform";
  import type { PredictionData } from "$api/predictions.remote";
  import type { GlucoseChartContext, LegendState } from "./chart-context.svelte";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    createChartDataEngine,
    TREATMENT_PROXIMITY_MS,
  } from "./engine/chart-data-engine.svelte";
  import { createPointInspection } from "./engine/point-inspection.svelte";
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
  import ChartHighlight from "./tracks/ChartHighlight.svelte";
  import ChartTooltip from "./ChartTooltip.svelte";

  // Dialogs
  import { EntryEditDialog } from "$lib/components/entries";
  import TreatmentDisambiguationDialog from "./dialogs/TreatmentDisambiguationDialog.svelte";
  import PointInspectionPicker from "./dialogs/PointInspectionPicker.svelte";
  import GlucoseInspectionDialog from "./dialogs/GlucoseInspectionDialog.svelte";
  import DeliveryInspectionDialog from "./dialogs/DeliveryInspectionDialog.svelte";
  import TreatmentInspectionDialog from "./dialogs/TreatmentInspectionDialog.svelte";

  interface Props {
    dateRange?: { from: Date | string; to: Date | string };
    focusHours?: number;
    initialChartData?: TransformedChartData | null;
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
  // svelte-ignore state_referenced_locally
  const engine = createChartDataEngine({
    dateRange,
    focusHours,
    initialChartData,
    streamedHistoricalData,
    externalPredictionData,
    enablePredictions,
    demoMode,
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
  let isEntryDialogOpen = $state(false);
  let nearbyEntries = $state<EntryRecord[]>([]);
  let isDisambiguationOpen = $state(false);

  // ---- Inspection dialog state ----
  // These mirror PointInspection.activeDialog for bind:open compatibility.
  // The $effect sync is intentional: dialogs need writable `open` props for
  // internal close behaviour (escape key, backdrop click), and the
  // PointInspection object is the authoritative source of truth.
  let isPickerOpen = $state(false);
  let isGlucoseInspectionOpen = $state(false);
  let isDeliveryInspectionOpen = $state(false);
  let isTreatmentInspectionOpen = $state(false);

  $effect(() => {
    if (!inspection) return;
    const dialog = inspection.activeDialog;
    isPickerOpen = dialog === "picker";
    isGlucoseInspectionOpen = dialog === "glucose";
    isDeliveryInspectionOpen = dialog === "delivery";
    isTreatmentInspectionOpen = dialog === "treatment";
  });

  // ---- Entry lookup helpers ----
  function findAllNearbyEntries(time: Date): EntryRecord[] {
    const nearby: EntryRecord[] = [];
    const seen = new Set<string>();

    const allMarkers = [
      ...engine.bolusMarkers,
      ...engine.carbMarkers,
      ...engine.deviceEventMarkers,
    ];

    for (const marker of allMarkers) {
      if (
        Math.abs(marker.time.getTime() - time.getTime()) <
        TREATMENT_PROXIMITY_MS
      ) {
        const entry = realtimeStore.findEntryByTreatmentId(
          marker.treatmentId ?? "",
        );
        if (entry && entry.data.id && !seen.has(entry.data.id)) {
          seen.add(entry.data.id);
          nearby.push(entry);
        }
      }
    }

    return nearby;
  }

  async function handleMarkerClick(treatmentId: string) {
    let entry: EntryRecord | null =
      realtimeStore.findEntryByTreatmentId(treatmentId) ?? null;

    if (!entry) {
      const result = await getEntryByTreatmentId({ treatmentId });
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
      isEntryDialogOpen = true;
    } else {
      nearbyEntries = nearby;
      isDisambiguationOpen = true;
    }
  }

  function selectEntryFromList(entry: EntryRecord) {
    isDisambiguationOpen = false;
    nearbyEntries = [];
    selectedEntry = entry;
    correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
    isEntryDialogOpen = true;
  }

  // ---- Inspection dialog handlers ----
  function handleInspectionSelect(type: "glucose" | "delivery" | "treatment") {
    inspection?.selectDialog(type);
  }

  function closeAllInspections() {
    inspection?.close();
  }
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
  {#snippet overlays(_ctx)}
    <ChartTooltip {tooltipExtras} />
  {/snippet}
</GlucoseChartShell>

<!-- Entry Edit Dialog -->
<EntryEditDialog
  bind:open={isEntryDialogOpen}
  entry={selectedEntry}
  {correlatedRecords}
  onClose={() => {
    isEntryDialogOpen = false;
    selectedEntry = null;
    correlatedRecords = [];
  }}
/>

<!-- Disambiguation Dialog -->
<TreatmentDisambiguationDialog
  bind:open={isDisambiguationOpen}
  entries={nearbyEntries}
  onSelect={selectEntryFromList}
  onClose={() => {
    isDisambiguationOpen = false;
    nearbyEntries = [];
  }}
/>

<!-- Point Inspection Dialogs -->
{#if inspection}
  <PointInspectionPicker
    bind:open={isPickerOpen}
    options={inspection.pickerOptions}
    onSelect={handleInspectionSelect}
    onClose={closeAllInspections}
  />

  {#if inspection.timestamp && inspection.glucosePoint && inspection.context}
    <GlucoseInspectionDialog
      bind:open={isGlucoseInspectionOpen}
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

    <DeliveryInspectionDialog
      bind:open={isDeliveryInspectionOpen}
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

    <TreatmentInspectionDialog
      bind:open={isTreatmentInspectionOpen}
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
