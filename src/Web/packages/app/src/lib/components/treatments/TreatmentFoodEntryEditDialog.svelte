<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Scale, Pencil } from "lucide-svelte";
  import {
    TreatmentFoodInputMode,
    type TreatmentFood,
    type TreatmentFoodRequest,
    type Food,
  } from "$lib/api";
  import {
    updateTreatmentFood,
    getFoodById,
    getAllFoods,
  } from "$lib/data/treatment-foods.remote";
  import { toast } from "svelte-sonner";
  import { AddFoodDialog } from "$lib/components/food";

  interface Props {
    /** Whether the dialog is open */
    open: boolean;
    /** Callback when open state changes */
    onOpenChange: (open: boolean) => void;
    /** The treatment food entry to edit */
    entry: TreatmentFood | null;
    /** The treatment ID */
    treatmentId: string | undefined;
    /** Total carbs from the treatment */
    totalCarbs?: number;
    /** Remaining unspecified carbs (excluding this entry) */
    remainingCarbs?: number;
    /** Callback when entry is updated */
    onSave?: () => void;
  }

  let {
    open = $bindable(),
    onOpenChange,
    entry,
    treatmentId,
    totalCarbs = 0,
    remainingCarbs = 0,
    onSave,
  }: Props = $props();

  // Edit form state
  let editPortions = $state(1);
  let editCarbs = $state<number>(0);
  let editOffset = $state<number | undefined>(0);
  let editNote = $state("");
  let isSaving = $state(false);

  // Track which field was last edited to determine calculation direction
  let lastEditedField = $state<"portions" | "carbs">("portions");

  // Edit food dialog state
  let showEditFoodDialog = $state(false);
  let foodToEdit = $state<Food | null>(null);
  let isLoadingFood = $state(false);
  let categories = $state<Record<string, Record<string, boolean>>>({});

  // Initialize form when entry changes
  $effect(() => {
    if (entry && open) {
      editPortions = entry.portions ?? 1;
      editCarbs = entry.carbs ?? 0;
      editOffset = entry.timeOffsetMinutes ?? 0;
      editNote = entry.note ?? "";
      lastEditedField = "portions";
    }
  });

  // Computed carbs per portion for the current entry
  const carbsPerPortion = $derived(entry?.carbsPerPortion ?? 0);

  // Check if this is an "Other" entry (no foodId)
  const isOtherEntry = $derived(entry && !entry.foodId);

  // Calculate actual unattributed carbs (remainingCarbs includes space for current entry)
  // So actual unattributed = remainingCarbs - current entry's carbs
  const actualUnattributed = $derived(
    Math.max(0, Math.round((remainingCarbs - (entry?.carbs ?? 0)) * 10) / 10)
  );

  // Handle portions input change - recalculate carbs
  function handlePortionsChange(value: number) {
    editPortions = value;
    lastEditedField = "portions";
    if (carbsPerPortion > 0) {
      editCarbs = Math.round(value * carbsPerPortion * 10) / 10;
    }
  }

  // Handle carbs input change - recalculate portions
  function handleCarbsChange(value: number) {
    editCarbs = value;
    lastEditedField = "carbs";
    if (carbsPerPortion > 0) {
      editPortions = Math.round((value / carbsPerPortion) * 100) / 100;
    }
  }

  // Scale food to fill remaining unattributed carbs
  function scaleToFit() {
    if (remainingCarbs > 0) {
      handleCarbsChange(Math.round(remainingCarbs * 10) / 10);
    }
  }

  // Open the edit food dialog
  async function openEditFood() {
    if (!entry?.foodId) return;

    isLoadingFood = true;
    try {
      // Fetch the food and categories in parallel
      const [food, allFoods] = await Promise.all([
        getFoodById(entry.foodId),
        getAllFoods(),
      ]);

      foodToEdit = food;

      // Build categories from all foods
      const catMap: Record<string, Record<string, boolean>> = {};
      for (const f of allFoods) {
        if (f.category) {
          if (!catMap[f.category]) {
            catMap[f.category] = {};
          }
          if (f.subcategory) {
            catMap[f.category][f.subcategory] = true;
          }
        }
      }
      categories = catMap;

      showEditFoodDialog = true;
    } catch (err) {
      console.error("Failed to load food for editing:", err);
      toast.error("Failed to load food");
    } finally {
      isLoadingFood = false;
    }
  }

  // Handle food saved - update the carbs per portion if changed
  function handleFoodSaved(updatedFood: Food) {
    // Update the local carbsPerPortion calculation based on new food data
    if (updatedFood.carbs !== undefined) {
      // Recalculate carbs based on current portions
      const newCarbsPerPortion = updatedFood.carbs;
      editCarbs = Math.round(editPortions * newCarbsPerPortion * 10) / 10;
    }
    // Trigger a refresh of the parent data
    onSave?.();
  }

  function resetAndClose() {
    editPortions = 1;
    editCarbs = 0;
    editOffset = 0;
    editNote = "";
    lastEditedField = "portions";
    isSaving = false;
    open = false;
    onOpenChange(false);
  }

  async function handleSave() {
    if (!treatmentId || !entry?.id) return;

    isSaving = true;
    try {
      const request: TreatmentFoodRequest = {
        foodId: entry.foodId ?? undefined,
        timeOffsetMinutes: editOffset,
        note: editNote.trim() || undefined,
        inputMode:
          lastEditedField === "portions"
            ? TreatmentFoodInputMode.Portions
            : TreatmentFoodInputMode.Carbs,
      };

      if (entry.foodId) {
        if (lastEditedField === "portions") {
          request.portions = editPortions;
        } else {
          request.carbs = editCarbs;
        }
      } else {
        // "Other" entries only have carbs
        request.carbs = editCarbs;
        request.inputMode = TreatmentFoodInputMode.Carbs;
      }

      await updateTreatmentFood({
        treatmentId,
        entryId: entry.id,
        request,
      });

      toast.success("Food entry updated");
      onSave?.();
      resetAndClose();
    } catch (err) {
      console.error("Failed to update food entry:", err);
      toast.error("Failed to update food entry");
    } finally {
      isSaving = false;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(value) => !value && resetAndClose()}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Edit Food Entry</Dialog.Title>
    </Dialog.Header>

    <div class="space-y-4">
      {#if entry}
        <!-- Show carbs context -->
        {#if totalCarbs > 0}
          <div class="space-y-1">
            <div
              class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
            >
              <span>Treatment total</span>
              <span class="font-semibold tabular-nums">{totalCarbs}g</span>
            </div>
            {#if actualUnattributed > 0}
              <div
                class="flex items-center justify-between px-3 py-1 text-xs text-muted-foreground"
              >
                <span>Remaining to attribute</span>
                <span class="tabular-nums">{actualUnattributed}g</span>
              </div>
            {/if}
          </div>
        {/if}

        <div
          class="flex items-center justify-between rounded-md border p-3 text-sm"
        >
          <div>
            <div class="font-medium">{entry.foodName ?? "Other"}</div>
            {#if entry.foodId && carbsPerPortion > 0}
              <div class="text-xs text-muted-foreground">
                {carbsPerPortion}g carbs per portion
              </div>
            {/if}
          </div>
          {#if entry.foodId}
            <Button
              type="button"
              variant="ghost"
              size="icon"
              class="h-8 w-8"
              onclick={openEditFood}
              disabled={isLoadingFood}
              title="Edit food"
            >
              <Pencil class="h-4 w-4" />
            </Button>
          {/if}
        </div>

        <div class="grid gap-4 md:grid-cols-2">
          {#if !isOtherEntry}
            <div class="space-y-2">
              <Label for="edit-portions">Portions</Label>
              <Input
                id="edit-portions"
                type="number"
                step="0.1"
                min="0"
                value={editPortions}
                oninput={(e) =>
                  handlePortionsChange(parseFloat(e.currentTarget.value) || 0)}
              />
            </div>
          {/if}
          <div class="space-y-2">
            <Label for="edit-carbs">Carbs (g)</Label>
            <Input
              id="edit-carbs"
              type="number"
              step="0.1"
              min="0"
              value={editCarbs}
              oninput={(e) =>
                handleCarbsChange(parseFloat(e.currentTarget.value) || 0)}
            />
          </div>
        </div>

        <!-- Scale to fit button -->
        {#if !isOtherEntry && remainingCarbs > 0 && carbsPerPortion > 0}
          <Button
            type="button"
            variant="outline"
            size="sm"
            class="w-full"
            onclick={scaleToFit}
          >
            <Scale class="mr-2 h-4 w-4" />
            {editCarbs > remainingCarbs ? "Scale down" : "Scale"} to fill remaining {remainingCarbs}g
          </Button>
        {/if}

        <div class="grid gap-4 md:grid-cols-2">
          <div class="space-y-2">
            <Label for="edit-offset">Time offset (min)</Label>
            <Input
              id="edit-offset"
              type="number"
              step="1"
              bind:value={editOffset}
            />
          </div>
          <div class="space-y-2">
            <Label for="edit-note">Note</Label>
            <Input id="edit-note" bind:value={editNote} />
          </div>
        </div>
      {/if}
    </div>

    <Dialog.Footer class="gap-2">
      <Button type="button" variant="outline" onclick={resetAndClose}>
        Cancel
      </Button>
      <Button type="button" onclick={handleSave} disabled={isSaving}>
        {isSaving ? "Saving..." : "Save"}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<AddFoodDialog
  bind:open={showEditFoodDialog}
  onOpenChange={(value) => {
    showEditFoodDialog = value;
    if (!value) foodToEdit = null;
  }}
  initialFood={foodToEdit}
  {categories}
  onSave={handleFoodSaved}
/>
