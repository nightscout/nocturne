<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import Star from "@lucide/svelte/icons/star";
  import { FoodFields, type FoodCategoryMap } from "#lib/components/food/index.js";
  import type { Food } from "#lib/api/index.js";

  interface Props {
    draft: Food;
    selectedFood: Food | null;
    favorites: Food[];
    categories: FoodCategoryMap;
    isCreatingNew: boolean;
    onToggleFavorite: () => void;
    onsubmit?: (event: SubmitEvent) => void;
  }

  let {
    draft = $bindable(),
    selectedFood,
    favorites,
    categories,
    isCreatingNew,
    onToggleFavorite,
    onsubmit,
  }: Props = $props();
</script>

<form
  id="food-form"
  class="@container border-t pt-4 space-y-4"
  {onsubmit}
>
  {#if selectedFood && !isCreatingNew}
    <div
      class="flex items-center justify-between rounded-lg border bg-muted/30 p-3"
    >
      <div class="flex items-center gap-4">
        <div class="text-sm">
          <span class="font-medium">{draft.portion}{draft.unit}</span>
          <span class="text-muted-foreground">=</span>
          <span class="font-semibold text-success">
            {draft.carbs}g carbs
          </span>
        </div>
        {#if (draft.fat ?? 0) > 0 || (draft.protein ?? 0) > 0}
          <div class="text-xs text-muted-foreground">
            {draft.fat ?? 0}g fat • {draft.protein ?? 0}g protein
          </div>
        {/if}
      </div>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        onclick={onToggleFavorite}
      >
        <Star
          class="h-4 w-4 {favorites.some((fav) => fav._id === selectedFood?._id)
            ? 'text-favorite fill-favorite'
            : 'text-muted-foreground'}"
        />
      </Button>
    </div>
  {/if}

  <FoodFields bind:draft {categories} collapseDetails />
</form>
