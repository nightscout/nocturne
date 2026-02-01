<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Pencil, Trash2, Plus } from "lucide-svelte";
  import {
    type TreatmentFood,
    type TreatmentFoodBreakdown,
    type TreatmentFoodRequest,
  } from "$lib/api";
  import {
    addTreatmentFood,
    deleteTreatmentFood,
    getTreatmentFoodBreakdown,
  } from "$lib/data/treatment-foods.remote";
  import TreatmentFoodSelectorDialog from "./TreatmentFoodSelectorDialog.svelte";
  import TreatmentFoodEntryEditDialog from "./TreatmentFoodEntryEditDialog.svelte";
  import { CarbBreakdownBar, FoodEntryDetails } from "./index";

  interface Props {
    treatmentId?: string;
    /**
     * Total carbs from the treatment - shown so user knows what they're working
     * toward
     */
    totalCarbs?: number;
  }

  let { treatmentId, totalCarbs = 0 }: Props = $props();

  let breakdown = $state<TreatmentFoodBreakdown | null>(null);
  let isLoading = $state(false);
  let loadError = $state<string | null>(null);
  let showAddFood = $state(false);
  let showEdit = $state(false);

  let editEntry = $state<TreatmentFood | null>(null);

  $effect(() => {
    if (!treatmentId) {
      breakdown = null;
      return;
    }
    void loadBreakdown(treatmentId);
  });

  async function loadBreakdown(id: string) {
    isLoading = true;
    loadError = null;
    try {
      breakdown = await getTreatmentFoodBreakdown(id);
    } catch (err) {
      console.error("Failed to load food breakdown:", err);
      loadError = "Unable to load food breakdown.";
    } finally {
      isLoading = false;
    }
  }

  async function handleAddFood(request: TreatmentFoodRequest) {
    if (!treatmentId) return;
    try {
      const updated = await addTreatmentFood({ treatmentId, request });
      breakdown = updated;
      showAddFood = false;
    } catch (err) {
      console.error("Failed to add food entry:", err);
    }
  }

  function openEdit(entry: TreatmentFood) {
    editEntry = entry;
    showEdit = true;
  }

  async function handleDelete(entry: TreatmentFood) {
    if (!treatmentId || !entry.id) return;
    try {
      await deleteTreatmentFood({
        treatmentId,
        entryId: entry.id,
      });
      // Reload the breakdown after deletion
      await loadBreakdown(treatmentId);
    } catch (err) {
      console.error("Failed to delete food entry:", err);
    }
  }

  async function handleEditSaved() {
    if (treatmentId) {
      await loadBreakdown(treatmentId);
    }
  }

  // Remaining carbs available (excluding current entry being edited)
  const remainingCarbs = $derived.by(() => {
    if (!breakdown) return totalCarbs;
    const otherAttributedCarbs =
      breakdown.foods
        ?.filter((f) => f.id !== editEntry?.id)
        .reduce((sum, f) => sum + (f.carbs ?? 0), 0) ?? 0;
    return Math.round((totalCarbs - otherAttributedCarbs) * 10) / 10;
  });
</script>

<div class="rounded-lg border p-4 space-y-4">
  <div class="flex items-center justify-between">
    <div class="space-y-1">
      <div class="text-sm font-semibold">Food Breakdown</div>
      <div class="text-xs text-muted-foreground">
        Add foods to match carbs when it helps. Partial attribution is fine.
      </div>
    </div>
    <Button type="button" size="sm" onclick={() => (showAddFood = true)}>
      <Plus class="mr-1 h-4 w-4" />
      Add Food
    </Button>
  </div>

  {#if isLoading}
    <div class="text-sm text-muted-foreground">Loading breakdown...</div>
  {:else if loadError}
    <div class="text-sm text-destructive">{loadError}</div>
  {:else if breakdown}
    {@const foodCount = breakdown.foods?.length ?? 0}
    {@const hasUnattributed = (breakdown.unspecifiedCarbs ?? 0) > 0}
    <div class="space-y-3">
      <!-- Total carbs indicator -->
      {#if totalCarbs > 0}
        <div
          class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2"
        >
          <span class="text-sm font-medium">Treatment Total</span>
          <span class="text-lg font-bold tabular-nums">{totalCarbs}g</span>
        </div>
      {/if}

      <!-- Carb breakdown bar: show only if multiple foods OR unattributed carbs with at least one food -->
      {#if totalCarbs > 0 && (foodCount > 1 || (foodCount >= 1 && hasUnattributed))}
        <CarbBreakdownBar {totalCarbs} foods={breakdown.foods ?? []} />
      {/if}

      <div class="flex flex-wrap gap-2 text-xs">
        <Badge variant="secondary">
          Attributed {breakdown.attributedCarbs}g
        </Badge>
        <Badge variant="outline">
          Unspecified {breakdown.unspecifiedCarbs}g
        </Badge>
      </div>

      {#if !breakdown.foods || breakdown.foods.length === 0}
        <div
          class="rounded-md border border-dashed p-4 text-sm text-muted-foreground"
        >
          No foods added yet.
        </div>
      {:else}
        <div class="space-y-2">
          {#each breakdown.foods as entry (entry.id)}
            <div class="flex items-start justify-between rounded-md border p-3">
              <div class="space-y-1">
                <div class="font-medium">
                  {entry.foodName ?? "Other"}
                </div>
                <FoodEntryDetails
                  food={entry}
                  class="text-xs text-muted-foreground"
                />
              </div>
              <div class="flex items-center gap-1">
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  onclick={() => openEdit(entry)}
                >
                  <Pencil class="h-4 w-4" />
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon"
                  class="text-destructive"
                  onclick={() => handleDelete(entry)}
                >
                  <Trash2 class="h-4 w-4" />
                </Button>
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </div>
  {/if}
</div>

<TreatmentFoodSelectorDialog
  bind:open={showAddFood}
  onOpenChange={(value) => (showAddFood = value)}
  onSubmit={handleAddFood}
  {totalCarbs}
  unspecifiedCarbs={breakdown?.unspecifiedCarbs ?? totalCarbs}
/>

<TreatmentFoodEntryEditDialog
  bind:open={showEdit}
  onOpenChange={(value) => {
    showEdit = value;
    if (!value) editEntry = null;
  }}
  entry={editEntry}
  {treatmentId}
  {totalCarbs}
  {remainingCarbs}
  onSave={handleEditSaved}
/>
