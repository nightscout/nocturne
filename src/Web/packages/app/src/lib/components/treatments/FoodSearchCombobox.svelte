<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import { Badge } from "$lib/components/ui/badge";
  import { Label } from "$lib/components/ui/label";
  import { Check, Plus, Star, Clock, FileText } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import type { Food } from "$lib/api";

  interface Props {
    allFoods: Food[];
    favorites: Food[];
    recents: Food[];
    selectedFood: Food | null;
    searchQuery: string;
    isLoading: boolean;
    onSelect: (food: Food) => void;
    onCreateNew: () => void;
    onLogWithoutSaving: () => void;
  }

  let {
    allFoods,
    favorites,
    recents,
    selectedFood,
    searchQuery = $bindable(),
    isLoading,
    onSelect,
    onCreateNew,
    onLogWithoutSaving,
  }: Props = $props();

  // Derived: filtered foods based on search
  const filteredFoods = $derived.by(() => {
    if (!searchQuery.trim()) return [];
    const query = searchQuery.trim().toLowerCase();
    return allFoods.filter((food) => {
      const name = food.name?.toLowerCase() ?? "";
      const category = food.category?.toLowerCase() ?? "";
      const subcategory = food.subcategory?.toLowerCase() ?? "";
      return (
        name.includes(query) ||
        category.includes(query) ||
        subcategory.includes(query)
      );
    });
  });

  // Derived: check if search matches any existing food name exactly
  const hasExactMatch = $derived(
    allFoods.some(
      (f) => f.name?.toLowerCase() === searchQuery.trim().toLowerCase()
    )
  );
</script>

<div class="space-y-2">
  <Label>Food</Label>

  <Command.Root shouldFilter={false}>
    <Command.Input placeholder="Search foods..." bind:value={searchQuery} />
    <Command.List class="max-h-[300px]">
      {#if isLoading}
        <Command.Empty>Loading foods...</Command.Empty>
      {:else if !searchQuery.trim()}
        <!-- Show favorites and recents when no search -->
        {#if favorites.length > 0}
          <Command.Group>
            <div
              class="px-2 py-1.5 text-xs font-medium text-muted-foreground flex items-center gap-1"
            >
              <Star class="h-3 w-3 text-favorite" />
              Favorites
            </div>
            {#each favorites as food (food._id)}
              <Command.Item
                value={food._id}
                onSelect={() => onSelect(food)}
                class="cursor-pointer"
              >
                <Check
                  class={cn(
                    "mr-2 h-4 w-4",
                    selectedFood?._id === food._id ? "opacity-100" : "opacity-0"
                  )}
                />
                <div class="flex-1">
                  <span>{food.name}</span>
                  <span class="ml-2 text-xs text-muted-foreground">
                    {food.carbs}g carbs
                  </span>
                </div>
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        {#if recents.length > 0}
          <Command.Group>
            <div
              class="px-2 py-1.5 text-xs font-medium text-muted-foreground flex items-center gap-1"
            >
              <Clock class="h-3 w-3" />
              Recent
            </div>
            {#each recents as food (food._id)}
              <Command.Item
                value={food._id}
                onSelect={() => onSelect(food)}
                class="cursor-pointer"
              >
                <Check
                  class={cn(
                    "mr-2 h-4 w-4",
                    selectedFood?._id === food._id ? "opacity-100" : "opacity-0"
                  )}
                />
                <div class="flex-1">
                  <span>{food.name}</span>
                  <span class="ml-2 text-xs text-muted-foreground">
                    {food.carbs}g carbs
                  </span>
                </div>
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        {#if favorites.length === 0 && recents.length === 0}
          <Command.Empty>
            Type to search foods or create a new one.
          </Command.Empty>
        {/if}
      {:else}
        <!-- Show filtered results and create option -->
        {#if filteredFoods.length > 0}
          <Command.Group>
            {#each filteredFoods as food (food._id)}
              <Command.Item
                value={food._id}
                onSelect={() => onSelect(food)}
                class="cursor-pointer"
              >
                <Check
                  class={cn(
                    "mr-2 h-4 w-4",
                    selectedFood?._id === food._id ? "opacity-100" : "opacity-0"
                  )}
                />
                <div class="flex-1">
                  <span>{food.name}</span>
                  <span class="ml-2 text-xs text-muted-foreground">
                    {food.carbs}g carbs
                  </span>
                </div>
                {#if food.category}
                  <Badge variant="outline">
                    {food.category}
                  </Badge>
                {/if}
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        <!-- Create new and log without saving options when searching -->
        {#if searchQuery.trim()}
          <Command.Group>
            {#if !hasExactMatch}
              <Command.Item
                value="__create_new__"
                onSelect={onCreateNew}
                class="cursor-pointer text-primary"
              >
                <Plus class="mr-2 h-4 w-4" />
                Create "{searchQuery.trim()}"
              </Command.Item>
            {/if}
            <Command.Item
              value="__log_without_saving__"
              onSelect={onLogWithoutSaving}
              class="cursor-pointer text-muted-foreground"
            >
              <FileText class="mr-2 h-4 w-4" />
              Log without saving food
            </Command.Item>
          </Command.Group>
        {/if}

        {#if filteredFoods.length === 0 && hasExactMatch}
          <Command.Empty>No additional matches found.</Command.Empty>
        {/if}
      {/if}
    </Command.List>
  </Command.Root>
</div>
