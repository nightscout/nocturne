<script lang="ts">
  import type { PointInspection } from "./engine/point-inspection.svelte";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getEntryByTreatmentId } from "$api/entries.remote";
  import { getGlucoseChartContext } from "./chart-context.svelte";
  import { TREATMENT_PROXIMITY_MS } from "./engine/chart-data-engine.svelte";
  import { EntryEditDialog } from "$lib/components/entries";
  import TreatmentDisambiguationDialog from "./dialogs/TreatmentDisambiguationDialog.svelte";
  import PointInspectionPicker from "./dialogs/PointInspectionPicker.svelte";
  import GlucoseInspectionDialog from "./dialogs/GlucoseInspectionDialog.svelte";
  import DeliveryInspectionDialog from "./dialogs/DeliveryInspectionDialog.svelte";
  import TreatmentInspectionDialog from "./dialogs/TreatmentInspectionDialog.svelte";

  interface Props {
    inspection: PointInspection;
  }

  let { inspection }: Props = $props();

  const realtimeStore = getRealtimeStore();
  const { engine } = getGlucoseChartContext();

  // Entry edit / disambiguation state
  let selectedEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let isEntryDialogOpen = $state(false);
  let nearbyEntries = $state<EntryRecord[]>([]);
  let isDisambiguationOpen = $state(false);

  // Dialog open states derived from inspection.activeDialog
  let isPickerOpen = $derived(inspection.activeDialog === "picker");
  let isGlucoseInspectionOpen = $derived(inspection.activeDialog === "glucose");
  let isDeliveryInspectionOpen = $derived(
    inspection.activeDialog === "delivery",
  );
  let isTreatmentInspectionOpen = $derived(
    inspection.activeDialog === "treatment",
  );

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
    // Fast path: check in-memory store first
    let entry: EntryRecord | null =
      realtimeStore.findEntryByTreatmentId(treatmentId) ?? null;

    // Slow path: fetch from API via remote function
    if (!entry) {
      const result = await getEntryByTreatmentId({ treatmentId });
      entry = result as EntryRecord | null;
    }

    if (!entry) {
      console.warn(
        `[InspectionDialogs] No entry found for treatmentId: ${treatmentId}`,
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
</script>

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

<!-- Point Inspection Picker -->
<PointInspectionPicker
  bind:open={isPickerOpen}
  options={inspection.pickerOptions}
  onSelect={(type) => inspection.selectDialog(type)}
  onClose={() => inspection.close()}
/>

<!-- Inspection Dialogs -->
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
    onClose={() => inspection.close()}
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
    onClose={() => inspection.close()}
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
    onClose={() => inspection.close()}
    onNavigateGlucose={() => inspection.navigateTo("glucose")}
    onNavigateDelivery={() => inspection.navigateTo("delivery")}
    onEditEntry={() => {
      if (inspection.context?.nearbyBolus?.treatmentId) {
        handleMarkerClick(inspection.context.nearbyBolus.treatmentId);
      } else if (inspection.context?.nearbyCarbs?.treatmentId) {
        handleMarkerClick(inspection.context.nearbyCarbs.treatmentId);
      }
    }}
  />
{/if}
