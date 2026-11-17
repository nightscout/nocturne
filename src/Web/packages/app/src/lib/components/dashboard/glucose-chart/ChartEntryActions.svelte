<script lang="ts">
  import { lazyComponent } from "#lib/utils/lazy-component.svelte.js";
  import type { EntryRecord } from "#lib/constants/entry-categories.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { getEntryByTreatmentId } from "#lib/api/entries.remote.js";
  import { fresh } from "#lib/api/fresh-query.js";
  import { canLogEvents } from "#lib/components/entries/can-log-events.js";
  import type { PointInspection } from "./engine/point-inspection.svelte";
  import PointActionsDialog from "./dialogs/PointActionsDialog.svelte";

  interface Props {
    /** The chart's point inspection; its picker step is this component's dialog. */
    inspection?: PointInspection;
    /** The entries recorded near a time, as the chart's markers show them. */
    findNearby: (time: Date) => EntryRecord[];
  }

  let { inspection, findNearby }: Props = $props();

  const realtimeStore = getRealtimeStore();
  const canLog = $derived(canLogEvents());

  /** Set when a marker click found several entries; the dialog then lists only those. */
  let markerPick = $state<{ time: Date; entries: EntryRecord[] } | null>(null);

  const pickingPoint = $derived(inspection?.activeDialog === "picker");
  let actionsOpen = $derived(pickingPoint || markerPick != null);
  const pointTime = $derived(markerPick?.time ?? inspection?.timestamp ?? null);
  const inspectOptions = $derived(markerPick ? [] : (inspection?.pickerOptions ?? []));
  const entries = $derived.by(() => {
    if (markerPick) return markerPick.entries;
    if (pickingPoint && canLog && inspection?.timestamp) return findNearby(inspection.timestamp);
    return [];
  });

  let editEntry = $state<EntryRecord | null>(null);
  let correlatedRecords = $state<EntryRecord[]>([]);
  let newEntryMills = $state<number | undefined>(undefined);

  const entryEditDialog = lazyComponent(
    () => import("#lib/components/entries/EntryEditDialog.svelte"),
  );

  function closeActions() {
    markerPick = null;
    // A dialog closing because an inspection view replaced it leaves that view open.
    if (inspection?.activeDialog === "picker") inspection.close();
  }

  function openEditor(entry: EntryRecord) {
    closeActions();
    editEntry = entry;
    newEntryMills = undefined;
    correlatedRecords = realtimeStore.findCorrelatedEntries(entry);
    entryEditDialog.open = true;
  }

  function openNewEntry() {
    const at = pointTime?.getTime();
    closeActions();
    editEntry = null;
    correlatedRecords = [];
    newEntryMills = at;
    entryEditDialog.open = true;
  }

  function closeEditor() {
    entryEditDialog.open = false;
    editEntry = null;
    correlatedRecords = [];
    newEntryMills = undefined;
  }

  /**
   * Edits the entry behind a treatment marker, or, when other entries sit within the marker's
   * proximity, lists them all to choose from.
   */
  export async function editTreatment(treatmentId: string): Promise<void> {
    let entry: EntryRecord | null = realtimeStore.findEntryByTreatmentId(treatmentId) ?? null;

    if (!entry) {
      const result = await fresh(getEntryByTreatmentId({ treatmentId }));
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- generated entry shape bridged to the app EntryRecord union
      entry = result as EntryRecord | null;
    }

    if (!entry) {
      console.warn(`[ChartEntryActions] No entry found for treatmentId: ${treatmentId}`);
      return;
    }

    const time = new Date(entry.data.mills ?? 0);
    const nearby = findNearby(time);

    if (nearby.length <= 1) {
      openEditor(entry);
    } else {
      markerPick = { time, entries: nearby };
    }
  }
</script>

<PointActionsDialog
  bind:open={actionsOpen}
  timestamp={pointTime}
  {inspectOptions}
  {entries}
  {canLog}
  onInspect={(type) => {
    markerPick = null;
    inspection?.selectDialog(type);
  }}
  onEdit={openEditor}
  onLog={openNewEntry}
  onClose={closeActions}
/>

{#if entryEditDialog.component}
  <entryEditDialog.component
    bind:open={entryEditDialog.open}
    entry={editEntry}
    {correlatedRecords}
    initialMills={newEntryMills}
    onClose={closeEditor}
  />
{/if}
