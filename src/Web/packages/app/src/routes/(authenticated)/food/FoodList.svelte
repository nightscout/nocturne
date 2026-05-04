<script lang="ts">
  import { getFoodState } from './food-context';
  import FoodRow from './FoodRow.svelte';
  import FoodListHeader from './FoodListHeader.svelte';
  import InlineEditor from './InlineEditor.svelte';
  import type { Food } from '$api';

  const state = getFoodState();
  const PAGE_SIZE = 60;

  function handleToggle(foodId: string | undefined) {
    if (!foodId) return;
    state.expandedId = state.expandedId === foodId ? null : foodId;
  }

  async function handleSave(draft: Food) {
    await state.saveFood(draft);
  }
</script>

<FoodListHeader sort={state.sort} onsort={(s) => (state.sort = s)} />

<div>
  {#each state.filteredFoods.slice(0, PAGE_SIZE) as food (food._id)}
    <FoodRow
      {food}
      expanded={state.expandedId === food._id}
      favorite={state.isFavorite(food._id)}
      ontoggle={() => handleToggle(food._id)}
      onfavorite={() => state.toggleFavorite(food._id)}
    />
    {#if state.expandedId === food._id}
      <InlineEditor
        {food}
        onsave={handleSave}
        oncancel={() => (state.expandedId = null)}
      />
    {/if}
  {/each}

  {#if state.filteredFoods.length > PAGE_SIZE}
    <div class="border-t border-border py-3.5 text-center text-xs text-muted-foreground/60">
      Showing {PAGE_SIZE} of {state.filteredFoods.length} — scroll or refine search to see more
    </div>
  {/if}
</div>
