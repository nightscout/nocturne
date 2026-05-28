<script lang="ts">
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import { ChevronDown, X, Check } from "lucide-svelte";
  import * as Popover from "$lib/components/ui/popover";
  import * as Command from "$lib/components/ui/command";
  import { cn } from "$lib/utils";

  interface Props {
    dateRange: { from?: string; to?: string };
    filterMode: "all" | "unattributed";
    searchQuery: string;
    selectedFoods: string[];
    uniqueFoods: string[];
    onClearFilters: () => void;
  }

  let {
    dateRange = $bindable(),
    filterMode = $bindable(),
    searchQuery = $bindable(),
    selectedFoods = $bindable(),
    uniqueFoods,
    onClearFilters,
  }: Props = $props();


  let foodFilterOpen = $state(false);
  let foodFilterSearch = $state("");

  function handleDateChange(params: { from?: string; to?: string }) {
    dateRange = { from: params.from, to: params.to };
  }

  function toggleFoodFilter(food: string) {
    if (selectedFoods.includes(food)) {
      selectedFoods = selectedFoods.filter((f) => f !== food);
    } else {
      selectedFoods = [...selectedFoods, food];
    }
  }

  function clearFoodFilter() {
    selectedFoods = [];
  }

  const filteredFoodsForDropdown = $derived.by(() => {
    if (!foodFilterSearch.trim()) return uniqueFoods;
    const search = foodFilterSearch.toLowerCase();
    return uniqueFoods.filter((food) => food.toLowerCase().includes(search));
  });

  const hasActiveFilters = $derived(
    searchQuery.trim() !== "" || selectedFoods.length > 0
  );
</script>

<Card.Root>
  <Card.Content class="@container space-y-4 p-4">
    <!-- Row 1: Date picker inline -->
    <DateRangePicker defaultDays={1} onDateChange={handleDateChange} />

    <!-- Row 2: All filter controls -->
    <div
      class="flex flex-col gap-4 @lg:flex-row @lg:items-center @lg:justify-between"
    >
      <!-- Left side: All/Unattributed, Search, Food filter -->
      <div class="flex flex-wrap items-center gap-2">
        <!-- All/Unattributed toggle -->
        <div class="flex items-center gap-1">
          <Button
            type="button"
            size="sm"
            variant={filterMode === "all" ? "default" : "outline"}
            onclick={() => (filterMode = "all")}
          >
            All
          </Button>
          <Button
            type="button"
            size="sm"
            variant={filterMode === "unattributed" ? "default" : "outline"}
            onclick={() => (filterMode = "unattributed")}
          >
            Unattributed only
          </Button>
        </div>

        <!-- Separator -->
        <div class="hidden @lg:block h-6 w-px bg-border"></div>

        <!-- Search -->
        <div class="flex-1 min-w-[200px] max-w-sm">
          <input
            type="text"
            placeholder="Search meals..."
            bind:value={searchQuery}
            class="flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
          />
        </div>

        <!-- Food Name Filter -->
        <Popover.Root bind:open={foodFilterOpen}>
          <Popover.Trigger>
            {#snippet child({ props = {} as Record<string, any> })}
              <Button variant="outline" size="sm" class="gap-2" {...props}>
                Foods
                {#if selectedFoods.length > 0}
                  <Badge variant="secondary" class="ml-1">
                    {selectedFoods.length}
                  </Badge>
                {/if}
                <ChevronDown class="h-4 w-4" />
              </Button>
            {/snippet}
          </Popover.Trigger>
          <Popover.Content class="w-[280px] p-0" align="start">
            <Command.Root shouldFilter={false}>
              <Command.Input
                placeholder="Search foods..."
                bind:value={foodFilterSearch}
              />
              <Command.List class="max-h-[200px]">
                <Command.Empty>No foods found.</Command.Empty>
                <Command.Group>
                  {#each filteredFoodsForDropdown as food}
                    <Command.Item
                      value={food}
                      onSelect={() => toggleFoodFilter(food)}
                      class="cursor-pointer"
                    >
                      <div
                        class={cn(
                          "mr-2 h-4 w-4 shrink-0 border rounded flex items-center justify-center",
                          selectedFoods.includes(food)
                            ? "bg-primary border-primary"
                            : "border-muted"
                        )}
                      >
                        {#if selectedFoods.includes(food)}
                          <Check class="h-3 w-3 text-primary-foreground" />
                        {/if}
                      </div>
                      <span class="truncate">{food}</span>
                    </Command.Item>
                  {/each}
                </Command.Group>
              </Command.List>
              {#if selectedFoods.length > 0}
                <div class="border-t p-2">
                  <Button
                    variant="ghost"
                    size="sm"
                    class="w-full"
                    onclick={clearFoodFilter}
                  >
                    <X class="mr-2 h-3 w-3" />
                    Clear filter
                  </Button>
                </div>
              {/if}
            </Command.Root>
          </Popover.Content>
        </Popover.Root>
      </div>

      <!-- Right side: Clear all filters -->
      {#if hasActiveFilters}
        <Button variant="ghost" size="sm" onclick={onClearFilters}>
          <X class="mr-1 h-4 w-4" />
          Clear filters
        </Button>
      {/if}
    </div>

    <!-- Active filters display -->
    {#if hasActiveFilters}
      <div class="flex flex-wrap items-center gap-2 pt-3 border-t text-sm">
        <span class="text-muted-foreground">Showing:</span>
        <span class="font-medium">
          <!-- Note: parent should track filteredAndSortedMeals count -->
        </span>

        {#each selectedFoods as food}
          <Badge variant="outline" class="gap-1">
            {food}
            <button
              onclick={() => toggleFoodFilter(food)}
              class="ml-1 hover:text-foreground"
            >
              <X class="h-3 w-3" />
            </button>
          </Badge>
        {/each}

        {#if searchQuery.trim()}
          <Badge variant="outline" class="gap-1">
            "{searchQuery}"
            <button
              onclick={() => (searchQuery = "")}
              class="ml-1 hover:text-foreground"
            >
              <X class="h-3 w-3" />
            </button>
          </Badge>
        {/if}
      </div>
    {/if}
  </Card.Content>
</Card.Root>
