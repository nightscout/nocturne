<script lang="ts">
  import type { Treatment } from "$lib/api";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Badge } from "$lib/components/ui/badge";
  import { formatTime } from "$lib/utils";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { TreatmentEditDialog } from "$lib/components/treatments";
  import { updateTreatment } from "$lib/data/treatments.remote";
  import { toast } from "svelte-sonner";

  interface ComponentProps {
    treatments?: Treatment[];
    maxTreatments?: number;
    title?: string;
    subtitle?: string;
  }

  let {
    treatments,
    maxTreatments = 5,
    title = "Recent Treatments",
    subtitle = "Last 6 hours",
  }: ComponentProps = $props();

  const realtimeStore = getRealtimeStore();

  // Use realtime store treatments as fallback when treatments prop not provided
  // Filter and limit treatments (commented out time filtering from original, keeping limit only)
  const displayTreatments = $derived(
    (treatments ?? realtimeStore.treatments)
      // .filter((t) => {
      //   const treatmentTime = t.mills || new Date().getTime();
      //   return treatmentTime > Date.now() - 6 * 60 * 60 * 1000;
      // })
      .slice(0, maxTreatments)
  );

  let selectedTreatment = $state<Treatment | null>(null);
  let isDialogOpen = $state(false);
  let isUpdating = $state(false);

  function handleTreatmentClick(treatment: Treatment) {
    selectedTreatment = treatment;
    isDialogOpen = true;
  }

  async function handleSave(updatedTreatment: Treatment) {
    isUpdating = true;
    try {
      await updateTreatment({ ...updatedTreatment });
      toast.success("Treatment updated");
      isDialogOpen = false;
      selectedTreatment = null;
    } catch (e) {
      console.error(e);
      toast.error("Failed to update treatment");
    } finally {
      isUpdating = false;
    }
  }
</script>

<Card class="@container">
  <svelte:boundary>
    {#snippet pending()}
      <div class="flex items-center justify-center h-full">
        <div
          class="animate-spin rounded-full h-8 w-8 border-b-2 border-gray-900"
        ></div>
      </div>
    {/snippet}
    {#snippet failed(_error)}
      <p class="text-red-500 text-center">Error loading recent treatments.</p>
    {/snippet}
    <CardHeader class="px-3 @md:px-6">
      <CardTitle>{title}</CardTitle>
      <p class="text-sm text-muted-foreground">{subtitle}</p>
    </CardHeader>
    <CardContent class="px-3 @md:px-6">
      {#if displayTreatments.length > 0}
        <div class="space-y-2 @md:space-y-3">
          {#each displayTreatments as treatment (treatment._id || treatment.mills)}
            <div
              class="flex items-center justify-between p-2 @md:p-3 bg-muted rounded-lg cursor-pointer hover:bg-muted/80 transition-colors"
              onclick={() => handleTreatmentClick(treatment)}
              role="button"
              tabindex="0"
              onkeydown={(e) => {
                if (e.key === "Enter" || e.key === " ") {
                  e.preventDefault();
                  handleTreatmentClick(treatment);
                }
              }}
            >
              <div class="flex items-center gap-2 @md:gap-3">
                <Badge variant="outline" class="text-xs @md:text-sm">{treatment.eventType}</Badge>
                <div>
                  <div class="font-medium">
                    {#if treatment.carbs}
                      {treatment.carbs}g carbs
                    {/if}
                    {#if treatment.insulin}
                      {treatment.insulin}u insulin
                    {/if}
                    {#if treatment.notes}
                      - {treatment.notes}
                    {/if}
                  </div>
                  <div class="text-sm text-muted-foreground">
                    {formatTime(treatment.mills!)}
                  </div>
                </div>
              </div>
              <div class="text-sm text-muted-foreground">
                {treatment.enteredBy || "Unknown"}
              </div>
            </div>
          {/each}
        </div>
      {:else}
        <p class="text-muted-foreground text-center py-8">
          No recent treatments
        </p>
      {/if}
    </CardContent>
  </svelte:boundary>
</Card>

<TreatmentEditDialog
  bind:open={isDialogOpen}
  treatment={selectedTreatment}
  isLoading={isUpdating}
  onClose={() => {
    isDialogOpen = false;
    selectedTreatment = null;
  }}
  onSave={handleSave}
/>
