<script lang="ts">
  import { FoodState } from "./food-state.svelte.js";
  import { setFoodState } from "./food-context.js";
  import FoodEditor from "./FoodEditor.svelte";
  import QuickPicksList from "./QuickPicksList.svelte";
  import type { FoodRecord, QuickPickRecord } from "./types";
  import FoodList from "./FoodList.svelte";
  import { getFoodData } from "./data.remote";

  // Fetch food data using remote function
  const foodData = await getFoodData();

  // Create and set the store in context
  const foodState = new FoodState(foodData);
  setFoodState(foodState);

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
</script>

<svelte:head>
  <title>Food Editor - Nightscout</title>
</svelte:head>

<div class="container mx-auto p-4 space-y-6">
  <div class="flex items-center justify-between">
    <h1 class="text-3xl font-bold">Food Editor</h1>
    {#if foodState.status}
      <div class="text-sm text-muted-foreground">{foodState.status}</div>
    {/if}
  </div>

  {#if foodState.loading}
    <div class="text-center py-8">Loading food database...</div>
  {:else}
    <!-- Food Database Section -->
    <FoodList {handleFoodDragStart} {handleFoodDragEnd} />

    <!-- Food Record Editor -->
    <FoodEditor />

    <!-- Quick Picks Section -->
    <QuickPicksList />
  {/if}
</div>
