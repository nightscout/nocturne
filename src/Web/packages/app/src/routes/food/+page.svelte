<script lang="ts">
  import { FoodState } from "./food-state.svelte.js";
  import { setFoodState } from "./food-context.js";
  import FoodEditor from "./FoodEditor.svelte";
  import QuickPicksList from "./QuickPicksList.svelte";
  import type { FoodRecord } from "./types";
  import FoodList from "./FoodList.svelte";
  import { getFoodData } from "./data.remote";
  import { onMount } from "svelte";
  import { AddFoodDialog } from "$lib/components/food";
  import { Button } from "$lib/components/ui/button";
  import { Plus } from "lucide-svelte";
  import type { Food } from "$lib/api";
  import FoodDeleteDialog from "./FoodDeleteDialog.svelte";
  import { getFoodAttributionCount } from "$api/generated/foods.generated.remote";

  // Create store with empty initial data - setContext must be called synchronously
  const foodState = new FoodState({
    foodList: [],
    quickPickList: [],
    categories: {},
  });
  setFoodState(foodState);

  // Dialog state
  let showAddFoodDialog = $state(false);

  // Delete dialog state
  let showDeleteDialog = $state(false);
  let deleteTarget = $state<FoodRecord | null>(null);
  let deleteAttributionCount = $state(0);
  let isDeleting = $state(false);

  // Fetch food data asynchronously and update the store
  onMount(async () => {
    foodState.loading = true;
    try {
      const data = await getFoodData();
      // Update the store with fetched data
      foodState.foodList = data.foodList;
      foodState.quickPickList = data.quickPickList;
      foodState.categories = data.categories;
      foodState.status = "Database loaded";
    } catch (error) {
      foodState.status = "Failed to load food database";
      console.error("Failed to load food data:", error);
    } finally {
      foodState.loading = false;
    }
  });

  // Drag and drop handlers (these stay in the main component as they handle DOM events)
  function handleFoodDragStart(event: DragEvent, food: FoodRecord) {
    if (!event.dataTransfer) return;

    event.dataTransfer.effectAllowed = "copy";
    event.dataTransfer.setData("application/json", JSON.stringify(food));

    // Visual feedback
    setTimeout(() => {
      const target = event.target as HTMLElement;
      target.classList.add("opacity-50");
    }, 0);
  }

  function handleFoodDragEnd(event: DragEvent) {
    const target = event.target as HTMLElement;
    target.classList.remove("opacity-50");
  }

  function handleFoodSaved(food: Food) {
    // Add the new food to the list
    const newFoodRecord = food as unknown as FoodRecord;
    const existingIndex = foodState.foodList.findIndex(
      (f) => f._id === food._id
    );
    if (existingIndex >= 0) {
      foodState.foodList[existingIndex] = newFoodRecord;
    } else {
      foodState.foodList = [...foodState.foodList, newFoodRecord];
    }

    // Update categories if new
    if (food.category) {
      if (!foodState.categories[food.category]) {
        foodState.categories[food.category] = {};
      }
      if (food.subcategory) {
        foodState.categories[food.category][food.subcategory] = true;
      }
    }
  }

  function handleCategoryCreate(category: string) {
    if (!foodState.categories[category]) {
      foodState.categories[category] = {};
    }
  }

  function handleSubcategoryCreate(category: string, subcategory: string) {
    if (!foodState.categories[category]) {
      foodState.categories[category] = {};
    }
    foodState.categories[category][subcategory] = true;
  }

  async function handleDeleteRequest(food: FoodRecord) {
    deleteTarget = food;
    deleteAttributionCount = 0;
    showDeleteDialog = true;

    // Fetch attribution count in background
    if (food._id) {
      try {
        const result = await getFoodAttributionCount(food._id);
        deleteAttributionCount = result?.count ?? 0;
      } catch {
        // If we can't get the count, show 0 (simple confirmation)
      }
    }
  }

  async function handleDeleteConfirm(mode: "clear" | "remove") {
    if (!deleteTarget) return;
    isDeleting = true;
    try {
      await foodState.deleteFood(deleteTarget, mode);
      showDeleteDialog = false;
      deleteTarget = null;
    } finally {
      isDeleting = false;
    }
  }
</script>

<svelte:head>
  <title>Food Editor - Nightscout</title>
</svelte:head>

<div class="container mx-auto p-4 space-y-6">
  <div class="flex items-center justify-between">
    <h1 class="text-3xl font-bold">Food Editor</h1>
    <div class="flex items-center gap-2">
      {#if foodState.status}
        <div class="text-sm text-muted-foreground">{foodState.status}</div>
      {/if}
      <Button onclick={() => (showAddFoodDialog = true)}>
        <Plus class="mr-1 h-4 w-4" />
        Add Food
      </Button>
    </div>
  </div>

  {#if foodState.loading}
    <div class="text-center py-8">Loading food database...</div>
  {:else}
    <!-- Food Database Section -->
    <FoodList {handleFoodDragStart} {handleFoodDragEnd} onDeleteRequest={handleDeleteRequest} />

    <!-- Food Record Editor -->
    <FoodEditor />

    <!-- Quick Picks Section -->
    <QuickPicksList />
  {/if}
</div>

<AddFoodDialog
  bind:open={showAddFoodDialog}
  onOpenChange={(value) => (showAddFoodDialog = value)}
  categories={foodState.categories}
  onSave={handleFoodSaved}
  onCategoryCreate={handleCategoryCreate}
  onSubcategoryCreate={handleSubcategoryCreate}
/>

<FoodDeleteDialog
  bind:open={showDeleteDialog}
  food={deleteTarget}
  attributionCount={deleteAttributionCount}
  isLoading={isDeleting}
  onClose={() => { showDeleteDialog = false; deleteTarget = null; }}
  onConfirm={handleDeleteConfirm}
/>
