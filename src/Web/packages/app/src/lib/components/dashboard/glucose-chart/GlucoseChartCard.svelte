<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
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
  } from "$lib/stores/appearance-store.svelte";
  import type { PredictionDisplayMode } from "$lib/stores/appearance-store.svelte";
  import PredictionSettings from "../PredictionSettings.svelte";
  import MiniOverviewChart from "../MiniOverviewChart.svelte";
  import GlucoseChartShell from "./GlucoseChartShell.svelte";
  import ChartLegend from "./ChartLegend.svelte";
  import ZoomIndicator from "./ZoomIndicator.svelte";
  import { createChartDataEngine, TREATMENT_PROXIMITY_MS } from "./engine/chart-data-engine.svelte";
  import { createPointInspection } from "./engine/point-inspection.svelte";
  import { getEntryByTreatmentId } from "$api/entries.remote";
  import type { LegendState } from "./chart-context.svelte";
  import type { TransformedChartData } from "$lib/utils/chart-data-transform";
  import type { PredictionData } from "$api/predictions.remote";
  import { EntryEditDialog } from "$lib/components/entries";

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

  // Dialogs
  import TreatmentDisambiguationDialog from "./dialogs/TreatmentDisambiguationDialog.svelte";
  import PointInspectionPicker from "./dialogs/PointInspectionPicker.svelte";
  import GlucoseInspectionDialog from "./dialogs/GlucoseInspectionDialog.svelte";
  import DeliveryInspectionDialog from "./dialogs/DeliveryInspectionDialog.svelte";
  import TreatmentInspectionDialog from "./dialogs/TreatmentInspectionDialog.svelte";

  interface Props {
    dateRange?: { from: Date | string; to: Date | string };
    initialChartData?: TransformedChartData | null;
    streamedHistoricalData?: Promise<TransformedChartData | null>;
    externalPredictionData?: PredictionData | null;
    showPredictions?: boolean;
    defaultFocusHours?: number;
    heightClass?: string;
    demoMode?: boolean;
  }

  let {
    dateRange,
    initialChartData,
    streamedHistoricalData,
    externalPredictionData,
    showPredictions = true,
    defaultFocusHours,
    heightClass,
    demoMode,
  }: Props = $props();

  const realtimeStore = getRealtimeStore();
  const displayDemoMode = $derived(demoMode ?? realtimeStore.demoMode);

  // ===== ENGINE =====
  // svelte-ignore state_referenced_locally
  const engine = createChartDataEngine({
    dateRange,
    focusHours: defaultFocusHours,
    initialChartData,
    streamedHistoricalData,
    externalPredictionData,
    enablePredictions: showPredictions,
    demoMode,
  });

  // ===== POINT INSPECTION =====
  // svelte-ignore state_referenced_locally
  const inspection = createPointInspection(
    engine.finders,
    () => engine.glucoseData,
    {
      iobData: () => engine.iobData,
      cobData: () => engine.cobData,
      basalData: () => engine.basalData,
    },
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
  const effectiveShowPredictions = $derived(
    showPredictions && engine.effectiveShowPredictions,
  );

  let predictionModeValue = $state(predictionDisplayMode.current);

  function handlePredictionModeChange(value: PredictionDisplayMode) {
    if (value && value !== predictionModeValue) {
      predictionModeValue = value;
      predictionDisplayMode.current = value;
    }
  }

  // ===== ENTRY EDIT / MARKER CLICK =====
  let selectedEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let isEntryDialogOpen = $state(false);
  let nearbyEntries = $state<EntryRecord[]>([]);
  let isDisambiguationOpen = $state(false);

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
        `[GlucoseChartCard] No entry found for treatmentId: ${treatmentId}`,
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

  // ===== INSPECTION DIALOG STATE =====
  let isPickerOpen = $state(false);
  let isGlucoseInspectionOpen = $state(false);
  let isDeliveryInspectionOpen = $state(false);
  let isTreatmentInspectionOpen = $state(false);

  $effect(() => {
    const dialog = inspection.activeDialog;
    isPickerOpen = dialog === "picker";
    isGlucoseInspectionOpen = dialog === "glucose";
    isDeliveryInspectionOpen = dialog === "delivery";
    isTreatmentInspectionOpen = dialog === "treatment";
  });

  function handleInspectionSelect(type: "glucose" | "delivery" | "treatment") {
    inspection.selectDialog(type);
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
</script>

<Card class="@container bg-card border-border">
  <CardHeader class="pb-2 px-3 @md:px-6">
    <div class="flex items-center justify-between flex-wrap gap-2">
      <CardTitle class="flex items-center gap-2 text-card-foreground">
        Blood Glucose
        {#if displayDemoMode}
          <Badge
            variant="outline"
            class="text-xs border-border text-muted-foreground"
          >
            Demo
          </Badge>
        {/if}
      </CardTitle>

      <div class="flex items-center gap-2">
        <PredictionSettings
          showPredictions={effectiveShowPredictions}
          predictionMode={predictionModeValue}
          onPredictionModeChange={handlePredictionModeChange}
        />
      </div>
    </div>
  </CardHeader>

  <CardContent class="p-1 @md:p-2">
    <ZoomIndicator {isZoomed} brushXDomain={brushDomain} onResetZoom={resetZoom} />

    <div class={heightClass ?? "h-80 @md:h-[450px]"}>
      <GlucoseChartShell
        {engine}
        {inspection}
        {legend}
        brushDomain={brushDomain}
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
          {#if effectiveShowPredictions}
            <PredictionTrack />
          {/if}
          <IobCobTrack onMarkerClick={handleMarkerClick} />
          <DeviceEventMarkers onMarkerClick={handleMarkerClick} />
          <SystemEventMarkers />
          <TrackerMarkers />
          <ChartHighlight />
        {/snippet}
        {#snippet overlays(_ctx)}
          <ChartTooltip />
        {/snippet}
      </GlucoseChartShell>
    </div>

    {#if engine.glucoseData.length > 0}
      <MiniOverviewChart
        data={engine.glucoseData}
        fullXDomain={[engine.fullXDomain.from, engine.fullXDomain.to]}
        selectedXDomain={miniSelectedDomain}
        yDomain={[0, engine.glucoseYMax]}
        expanded={true}
        highThreshold={Number(engine.highThreshold)}
        lowThreshold={Number(engine.lowThreshold)}
        onSelectionChange={(domain) => handleMiniChartBrush(domain)}
        predictionData={miniPredictionData}
        showPredictions={effectiveShowPredictions && predictionEnabled.current}
      />
    {/if}

    <ChartLegend
      glucoseData={engine.glucoseData}
      highThreshold={engine.highThreshold}
      lowThreshold={engine.lowThreshold}
      veryHighThreshold={engine.veryHighThreshold}
      veryLowThreshold={engine.veryLowThreshold}
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
      systemEvents={engine.displaySystemEvents}
      pumpModeSpans={engine.displayPumpModeSpans}
      scheduledTrackerMarkers={engine.displayTrackerMarkers}
      currentPumpMode={engine.currentPumpMode}
      uniquePumpModes={engine.uniquePumpModes}
      {expandedPumpModes}
      onToggleExpandedPumpModes={() => (expandedPumpModes = !expandedPumpModes)}
    />
  </CardContent>
</Card>

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
