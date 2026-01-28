<script lang="ts">
  import {
    CurrentBGDisplay,
    GlucoseChartCard,
    RecentEntriesCard,
    RecentTreatmentsCard,
    WidgetGrid,
  } from "$lib/components/dashboard";
  import { TreatmentEditDialog } from "$lib/components/treatments";
  import { Button } from "$lib/components/ui/button";
  import { Plus } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getSettingsStore } from "$lib/stores/settings-store.svelte";
  import { dashboardTopWidgets } from "$lib/stores/appearance-store.svelte";
  import { WidgetId } from "$lib/api/generated/nocturne-api-client";
  import { isWidgetEnabled } from "$lib/types/dashboard-widgets";
  import { createTreatment } from "$lib/data/treatments.remote";
  import type { Treatment } from "$lib/api";

  const realtimeStore = getRealtimeStore();
  const settingsStore = getSettingsStore();

  // Get widgets array from settings (for main section visibility)
  const widgets = $derived(settingsStore.features?.widgets);

  // Helper to check if a main section is enabled
  const isMainEnabled = (id: (typeof WidgetId)[keyof typeof WidgetId]) =>
    isWidgetEnabled(widgets, id);

  // Get enabled top widgets from persisted appearance store
  const topWidgets = $derived(dashboardTopWidgets.current);

  // Get focusHours setting for chart default time range
  const focusHours = $derived(settingsStore.features?.display?.focusHours ?? 3);

  // Algorithm prediction settings - controls whether predictions are calculated
  const predictionEnabled = $derived(
    settingsStore.algorithm?.prediction?.enabled ?? true
  );

  // Treatment creation dialog
  let showCreateTreatment = $state(false);
  let isCreating = $state(false);

  async function handleCreateTreatment(treatment: Treatment) {
    isCreating = true;
    try {
      await createTreatment(treatment);
      showCreateTreatment = false;
      toast.success("Treatment created");
    } catch (err) {
      console.error("Failed to create treatment:", err);
      toast.error("Failed to create treatment");
    } finally {
      isCreating = false;
    }
  }
</script>

<div class="@container p-3 @md:p-6 space-y-3 @md:space-y-6">
  <CurrentBGDisplay />

  {#if isMainEnabled(WidgetId.Statistics)}
    <WidgetGrid widgets={topWidgets} maxWidgets={3} />
  {/if}

  {#if isMainEnabled(WidgetId.GlucoseChart)}
    <GlucoseChartCard
      entries={realtimeStore.entries}
      treatments={realtimeStore.treatments}
      showPredictions={isMainEnabled(WidgetId.Predictions) && predictionEnabled}
      defaultFocusHours={focusHours}
    />
  {/if}

  {#if isMainEnabled(WidgetId.DailyStats)}
    <RecentEntriesCard />
  {/if}

  {#if isMainEnabled(WidgetId.Treatments)}
    <RecentTreatmentsCard />
  {/if}
</div>

<!-- Treatment FAB -->
<Button
  onclick={() => (showCreateTreatment = true)}
  size="icon"
  class="fixed bottom-6 right-6 h-14 w-14 rounded-full shadow-lg z-50"
>
  <Plus class="h-6 w-6" />
  <span class="sr-only">New Treatment</span>
</Button>

<TreatmentEditDialog
  bind:open={showCreateTreatment}
  treatment={null}
  isLoading={isCreating}
  onClose={() => (showCreateTreatment = false)}
  onSave={handleCreateTreatment}
/>
