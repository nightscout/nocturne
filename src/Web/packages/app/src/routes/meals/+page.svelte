<script lang="ts">
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import {
    Calendar,
    ChevronDown,
    ChevronRight,
    ArrowUp,
    ArrowDown,
    ArrowUpDown,
    Filter,
    X,
    Check,
    Loader2,
    Sparkles,
    Utensils,
  } from "lucide-svelte";
  import * as Popover from "$lib/components/ui/popover";
  import * as Command from "$lib/components/ui/command";
  import type {
    MealCarbIntake,
    TreatmentFood,
    CarbIntakeFoodRequest,
    SuggestedMealMatch,
  } from "$lib/api";
  import { getMeals } from "$api/generated/nutritions.generated.remote";
  import { getSuggestions as getMealMatchingSuggestions, acceptMatch, dismissMatch } from "$api/generated/mealMatchings.generated.remote";
  import { toast } from "svelte-sonner";
  import {
    TreatmentFoodSelectorDialog,
    TreatmentFoodEntryEditDialog,
    CarbBreakdownBar,
    FoodEntryDetails,
  } from "$lib/components/treatments";
  import { addCarbIntakeFood } from "$api/generated/nutritions.generated.remote";
  import { getMealNameForTime } from "$lib/constants/meal-times";
  import { cn } from "$lib/utils";
  import { MealMatchReviewDialog } from "$lib/components/meal-matching";

  let dateRange = $state<{ from?: string; to?: string }>({});
  let filterMode = $state<"all" | "unattributed">("all");

  // Sorting state
  type SortColumn = "time" | "meal" | "carbs" | "insulin";
  type SortDirection = "asc" | "desc";
  let sortColumn = $state<SortColumn>("time");
  let sortDirection = $state<SortDirection>("desc");

  // Search and filter state
  let searchQuery = $state("");
  let selectedFoods = $state<string[]>([]);
  let foodFilterOpen = $state(false);
  let foodFilterSearch = $state("");

  let expandedRows = $state<Set<string>>(new Set());
  let collapsedDates = $state<Set<string>>(new Set());

  // Add food dialog state
  let showAddFoodDialog = $state(false);
  let addFoodMeal = $state<MealCarbIntake | null>(null);

  // Edit food entry dialog state
  let showEditFoodEntryDialog = $state(false);
  let editFoodEntry = $state<TreatmentFood | null>(null);
  let editFoodEntryMeal = $state<MealCarbIntake | null>(null);

  // Meal match review dialog state
  let showReviewDialog = $state(false);
  let reviewMatch = $state<SuggestedMealMatch | null>(null);

  function handleDateChange(params: { from?: string; to?: string }) {
    dateRange = { from: params.from, to: params.to };
  }

  const queryParams = $derived({
    from: dateRange.from ? new Date(dateRange.from).getTime() : undefined,
    to: dateRange.to ? new Date(dateRange.to).getTime() : undefined,
    attributed: filterMode === "unattributed" ? false : undefined,
  });

  const mealsQuery = $derived(getMeals(queryParams));
  const meals = $derived<MealCarbIntake[]>(mealsQuery.current ?? []);

  // Query for suggested meal matches using the endpoint
  const suggestionsQueryParams = $derived({
    from: dateRange.from ?? new Date().toISOString().split("T")[0],
    to: dateRange.to ?? new Date().toISOString().split("T")[0],
  });
  const suggestionsQuery = $derived(
    getMealMatchingSuggestions(suggestionsQueryParams)
  );
  const suggestedMatches = $derived<SuggestedMealMatch[]>(
    suggestionsQuery.current ?? []
  );

  // Loading state - check if data has arrived yet
  const isLoading = $derived(mealsQuery.current === undefined);

  // Create a map of carbIntakeId -> suggestions for easy lookup
  const suggestionsByCarbIntake = $derived.by(() => {
    const map = new Map<string, SuggestedMealMatch[]>();
    for (const match of suggestedMatches) {
      const treatmentId = match.treatmentId;
      if (!treatmentId) continue;
      if (!map.has(treatmentId)) {
        map.set(treatmentId, []);
      }
      map.get(treatmentId)!.push(match);
    }
    return map;
  });

  // Get unique food names for filter dropdown
  const uniqueFoods = $derived.by(() => {
    const foods = new Set<string>();
    for (const meal of meals) {
      for (const food of meal.foods ?? []) {
        if (food.foodName) foods.add(food.foodName);
      }
    }
    return Array.from(foods).sort();
  });

  const filteredFoodsForDropdown = $derived.by(() => {
    if (!foodFilterSearch.trim()) return uniqueFoods;
    const search = foodFilterSearch.toLowerCase();
    return uniqueFoods.filter((food) => food.toLowerCase().includes(search));
  });

  // Helper to get meal label for sorting
  function getMealSortLabel(meal: MealCarbIntake): string {
    const foods = meal.foods ?? [];
    if (foods.length === 0) return meal.carbIntake?.foodType ?? "Meal";
    if (foods.length === 1 && foods[0].foodName) return foods[0].foodName;
    return getMealNameForTime(
      new Date(meal.carbIntake?.mills ?? Date.now())
    );
  }

  // Filter and sort meals
  const filteredAndSortedMeals = $derived.by(() => {
    let filtered = meals;

    // Apply search filter
    if (searchQuery.trim()) {
      const query = searchQuery.toLowerCase();
      filtered = filtered.filter((meal) => {
        const searchable = [
          meal.carbIntake?.foodType,
          ...(meal.foods?.map((f) => f.foodName ?? f.note) ?? []),
        ]
          .filter(Boolean)
          .join(" ")
          .toLowerCase();
        return searchable.includes(query);
      });
    }

    // Apply food name filter
    if (selectedFoods.length > 0) {
      filtered = filtered.filter((meal) =>
        meal.foods?.some(
          (f) => f.foodName && selectedFoods.includes(f.foodName)
        )
      );
    }

    // Sort meals
    const sorted = [...filtered].sort((a, b) => {
      let comparison = 0;
      switch (sortColumn) {
        case "time":
          comparison =
            (a.carbIntake?.mills ?? 0) - (b.carbIntake?.mills ?? 0);
          break;
        case "meal":
          comparison = getMealSortLabel(a).localeCompare(getMealSortLabel(b));
          break;
        case "carbs":
          comparison =
            (a.carbIntake?.carbs ?? 0) - (b.carbIntake?.carbs ?? 0);
          break;
        case "insulin":
          comparison =
            (a.correlatedBolus?.insulin ?? 0) -
            (b.correlatedBolus?.insulin ?? 0);
          break;
      }
      return sortDirection === "asc" ? comparison : -comparison;
    });

    return sorted;
  });

  // Group meals by date for day separators
  interface MealsByDay {
    date: string;
    displayDate: string;
    meals: MealCarbIntake[];
  }

  const mealsByDay = $derived.by(() => {
    const grouped = new Map<string, MealCarbIntake[]>();

    for (const meal of filteredAndSortedMeals) {
      const mills = meal.carbIntake?.mills;
      if (!mills) continue;

      const date = new Date(mills);
      const dateKey = date.toLocaleDateString();

      if (!grouped.has(dateKey)) {
        grouped.set(dateKey, []);
      }
      grouped.get(dateKey)!.push(meal);
    }

    const result: MealsByDay[] = [];
    for (const [date, dayMeals] of grouped) {
      result.push({
        date,
        displayDate: new Date(
          dayMeals[0].carbIntake?.mills ?? 0
        ).toLocaleDateString(undefined, {
          weekday: "long",
          year: "numeric",
          month: "long",
          day: "numeric",
        }),
        meals: dayMeals,
      });
    }

    return result;
  });

  // Sorting and filtering helpers
  function toggleSort(column: SortColumn) {
    if (sortColumn === column) {
      sortDirection = sortDirection === "asc" ? "desc" : "asc";
    } else {
      sortColumn = column;
      sortDirection = column === "time" ? "desc" : "asc";
    }
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

  function clearAllFilters() {
    searchQuery = "";
    selectedFoods = [];
  }

  const hasActiveFilters = $derived(
    searchQuery.trim() !== "" || selectedFoods.length > 0
  );

  function toggleRow(id: string) {
    const newSet = new Set(expandedRows);
    if (newSet.has(id)) {
      newSet.delete(id);
    } else {
      newSet.add(id);
    }
    expandedRows = newSet;
  }

  function toggleDate(date: string) {
    const newSet = new Set(collapsedDates);
    if (newSet.has(date)) {
      newSet.delete(date);
    } else {
      newSet.add(date);
    }
    collapsedDates = newSet;
  }

  function openAddFood(meal: MealCarbIntake) {
    addFoodMeal = meal;
    showAddFoodDialog = true;
  }

  function openEditFoodEntry(meal: MealCarbIntake, food: TreatmentFood) {
    editFoodEntryMeal = meal;
    editFoodEntry = food;
    showEditFoodEntryDialog = true;
  }

  function getRemainingCarbsForEntry(
    meal: MealCarbIntake,
    entryId: string | undefined
  ): number {
    const totalCarbs = meal.carbIntake?.carbs ?? 0;
    const otherAttributedCarbs =
      meal.foods
        ?.filter((f) => f.id !== entryId)
        .reduce((sum, f) => sum + (f.carbs ?? 0), 0) ?? 0;
    return Math.round((totalCarbs - otherAttributedCarbs) * 10) / 10;
  }

  async function handleFoodEntrySaved() {
    await mealsQuery.refresh();
  }

  async function handleAddFoodSubmit(request: CarbIntakeFoodRequest) {
    if (!addFoodMeal?.carbIntake?.id) return;

    try {
      await addCarbIntakeFood({
        id: addFoodMeal.carbIntake.id,
        request,
      });
      toast.success("Food added");
      showAddFoodDialog = false;
      addFoodMeal = null;
      mealsQuery.refresh();
    } catch (err) {
      console.error("Add food error:", err);
      toast.error("Failed to add food");
    }
  }

  function formatTime(mills: number | undefined): string {
    if (!mills) return "—";
    return new Date(mills).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  function getMealLabel(meal: MealCarbIntake): string {
    const foods = meal.foods ?? [];
    if (foods.length === 0) {
      return meal.carbIntake?.foodType ?? "Meal";
    }
    if (foods.length === 1 && foods[0].foodName) {
      return foods[0].foodName;
    }
    // Multiple foods - use meal time-based name
    const date = new Date(meal.carbIntake?.mills ?? Date.now());
    return getMealNameForTime(date);
  }

  function getFoodsSummary(foods: TreatmentFood[] | undefined): string {
    if (!foods || foods.length === 0) return "No foods attributed";
    return foods.map((f) => f.foodName ?? f.note ?? "Other").join(", ");
  }

  // Suggested match handlers
  function openReviewDialog(match: SuggestedMealMatch) {
    reviewMatch = match;
    showReviewDialog = true;
  }

  async function handleQuickAccept(match: SuggestedMealMatch) {
    try {
      await acceptMatch({
        foodEntryId: match.foodEntryId!,
        treatmentId: match.treatmentId!,
        carbs: match.carbs ?? 0,
        timeOffsetMinutes: 0,
      });
      toast.success("Meal match accepted");
      mealsQuery.refresh();
      suggestionsQuery.refresh();
    } catch (err) {
      console.error("Failed to accept match:", err);
      toast.error("Failed to accept match");
    }
  }

  async function handleDismiss(match: SuggestedMealMatch) {
    try {
      await dismissMatch({ foodEntryId: match.foodEntryId! });
      toast.success("Match dismissed");
      suggestionsQuery.refresh();
    } catch (err) {
      console.error("Failed to dismiss match:", err);
      toast.error("Failed to dismiss match");
    }
  }

  function handleReviewComplete() {
    reviewMatch = null;
    mealsQuery.refresh();
    suggestionsQuery.refresh();
  }
</script>

<svelte:head>
  <title>Meals - Nocturne</title>
  <meta
    name="description"
    content="Review carb intake records and add food breakdowns for better meal documentation"
  />
</svelte:head>

<div class="container mx-auto space-y-6 px-4 py-6">
  <div class="space-y-2 text-center">
    <div
      class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
    >
      <Calendar class="h-4 w-4" />
      <span>Meals</span>
    </div>
    <h1 class="text-3xl font-bold">Meal Attribution</h1>
    <p class="text-muted-foreground">
      Pair carb intakes with foods when you want more detail.
    </p>
  </div>

  <!-- Consolidated filters section -->
  <Card.Root>
    <Card.Content class="space-y-4 p-4">
      <!-- Row 1: Date picker inline -->
      <DateRangePicker defaultDays={1} onDateChange={handleDateChange} />

      <!-- Row 2: All filter controls -->
      <div
        class="flex flex-col gap-4 md:flex-row md:items-center md:justify-between"
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
          <div class="hidden md:block h-6 w-px bg-border"></div>

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
              {#snippet child({ props })}
                <Button variant="outline" size="sm" class="gap-2" {...props}>
                  <Filter class="h-4 w-4" />
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
          <Button variant="ghost" size="sm" onclick={clearAllFilters}>
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
            {filteredAndSortedMeals.length} of {meals.length}
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

  <Card.Root>
    <Card.Content class="p-0">
      {#if isLoading}
        <div class="flex items-center justify-center p-12">
          <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
        </div>
      {:else if filteredAndSortedMeals.length === 0}
        <div class="p-6 text-center text-sm text-muted-foreground">
          {meals.length === 0
            ? "No meals found in this range."
            : "No meals match the current filters."}
        </div>
      {:else}
        <Table.Root>
          <Table.Header>
            <Table.Row>
              <Table.Head class="w-12"></Table.Head>
              <Table.Head class="w-24">
                <Button
                  variant="ghost"
                  size="sm"
                  class="-ml-3 h-8"
                  onclick={() => toggleSort("time")}
                >
                  Time
                  {#if sortColumn === "time"}
                    {#if sortDirection === "asc"}
                      <ArrowUp class="ml-1 h-4 w-4" />
                    {:else}
                      <ArrowDown class="ml-1 h-4 w-4" />
                    {/if}
                  {:else}
                    <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
                  {/if}
                </Button>
              </Table.Head>
              <Table.Head>
                <Button
                  variant="ghost"
                  size="sm"
                  class="-ml-3 h-8"
                  onclick={() => toggleSort("meal")}
                >
                  Meal
                  {#if sortColumn === "meal"}
                    {#if sortDirection === "asc"}
                      <ArrowUp class="ml-1 h-4 w-4" />
                    {:else}
                      <ArrowDown class="ml-1 h-4 w-4" />
                    {/if}
                  {:else}
                    <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
                  {/if}
                </Button>
              </Table.Head>
              <Table.Head class="w-24 text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  class="-mr-3 h-8"
                  onclick={() => toggleSort("carbs")}
                >
                  Carbs
                  {#if sortColumn === "carbs"}
                    {#if sortDirection === "asc"}
                      <ArrowUp class="ml-1 h-4 w-4" />
                    {:else}
                      <ArrowDown class="ml-1 h-4 w-4" />
                    {/if}
                  {:else}
                    <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
                  {/if}
                </Button>
              </Table.Head>
              <Table.Head class="w-32 text-right">
                <Button
                  variant="ghost"
                  size="sm"
                  class="-mr-3 h-8"
                  onclick={() => toggleSort("insulin")}
                >
                  Insulin
                  {#if sortColumn === "insulin"}
                    {#if sortDirection === "asc"}
                      <ArrowUp class="ml-1 h-4 w-4" />
                    {:else}
                      <ArrowDown class="ml-1 h-4 w-4" />
                    {/if}
                  {:else}
                    <ArrowUpDown class="ml-1 h-4 w-4 opacity-50" />
                  {/if}
                </Button>
              </Table.Head>
              <Table.Head class="w-32">Status</Table.Head>
              <Table.Head class="w-24"></Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each mealsByDay as day}
              {@const isDateCollapsed = collapsedDates.has(day.date)}
              {@const dayTotalCarbs = day.meals.reduce(
                (sum, m) => sum + (m.carbIntake?.carbs ?? 0),
                0
              )}
              {@const dayTotalInsulin = day.meals.reduce(
                (sum, m) => sum + (m.correlatedBolus?.insulin ?? 0),
                0
              )}
              <!-- Day separator row -->
              <Table.Row
                class="bg-muted/50 hover:bg-muted/60 cursor-pointer transition-colors"
                onclick={() => toggleDate(day.date)}
              >
                <Table.Cell class="py-2">
                  {#if isDateCollapsed}
                    <ChevronRight class="h-4 w-4 text-muted-foreground" />
                  {:else}
                    <ChevronDown class="h-4 w-4 text-muted-foreground" />
                  {/if}
                </Table.Cell>
                <Table.Cell colspan={2} class="py-2">
                  <div class="flex items-center gap-2 font-medium text-sm">
                    <Calendar class="h-4 w-4 text-muted-foreground" />
                    {day.displayDate}
                    <Badge variant="outline" class="ml-2">
                      {day.meals.length} meal{day.meals.length !== 1 ? "s" : ""}
                    </Badge>
                  </div>
                </Table.Cell>

                {#if isDateCollapsed}
                  <Table.Cell class="text-right py-2">
                    <span class="ml-auto tabular-nums text-muted-foreground">
                      {dayTotalCarbs}g
                    </span>
                  </Table.Cell>
                  <Table.Cell class="text-right py-2">
                    {#if dayTotalInsulin > 0}
                      <span class="tabular-nums text-muted-foreground">
                        {dayTotalInsulin.toFixed(1)}U
                      </span>
                    {/if}
                  </Table.Cell>
                {/if}
                <Table.Cell colspan={4} class="py-2"></Table.Cell>
              </Table.Row>

              {#if !isDateCollapsed}
                {#each day.meals as meal, mealIndex (`${day.date}-${mealIndex}-${meal.carbIntake?.id}`)}
                  {@const isExpanded = expandedRows.has(
                    meal.carbIntake?.id ?? ""
                  )}
                  {@const hasFoods = (meal.foods?.length ?? 0) > 0}
                  {@const totalCarbs = meal.carbIntake?.carbs ?? 0}
                  {@const mealSuggestions = suggestionsByCarbIntake.get(meal.carbIntake?.id ?? "") ?? []}

                  <!-- Main meal row -->
                  <Table.Row
                    class={cn(
                      "transition-colors",
                      hasFoods && "cursor-pointer",
                      isExpanded && "bg-accent/30"
                    )}
                    onclick={() =>
                      hasFoods && toggleRow(meal.carbIntake?.id ?? "")}
                  >
                    <Table.Cell class="py-3">
                      {#if hasFoods}
                        <Button
                          variant="ghost"
                          size="icon"
                          class="h-6 w-6"
                          onclick={(e) => {
                            e.stopPropagation();
                            toggleRow(meal.carbIntake?.id ?? "");
                          }}
                        >
                          {#if isExpanded}
                            <ChevronDown class="h-4 w-4" />
                          {:else}
                            <ChevronRight class="h-4 w-4" />
                          {/if}
                        </Button>
                      {/if}
                    </Table.Cell>
                    <Table.Cell class="py-3">
                      <div class="text-lg font-semibold tabular-nums">
                        {formatTime(meal.carbIntake?.mills)}
                      </div>
                    </Table.Cell>
                    <Table.Cell class="py-3">
                      <div class="flex items-center gap-2">
                        <Utensils class="h-4 w-4 text-muted-foreground" />
                        <div>
                          <div class="font-medium">{getMealLabel(meal)}</div>
                          {#if hasFoods}
                            <div
                              class="text-xs text-muted-foreground line-clamp-1"
                            >
                              {getFoodsSummary(meal.foods)}
                            </div>
                          {/if}
                        </div>
                      </div>
                    </Table.Cell>
                    <Table.Cell class="py-3">
                      <div class="flex items-center justify-end gap-3">
                        <button
                          type="button"
                          class="w-24 cursor-pointer hover:opacity-80 transition-opacity"
                          onclick={(e) => {
                            e.stopPropagation();
                            openAddFood(meal);
                          }}
                        >
                          <CarbBreakdownBar
                            {totalCarbs}
                            foods={meal.foods ?? []}
                          />
                        </button>
                        <span class="text-lg font-semibold tabular-nums">
                          {totalCarbs}g
                        </span>
                      </div>
                    </Table.Cell>
                    <Table.Cell class="py-3 text-right">
                      {#if meal.correlatedBolus?.insulin}
                        <span class="font-medium tabular-nums">
                          {meal.correlatedBolus.insulin.toFixed(1)}U
                        </span>
                      {:else}
                        <span class="text-muted-foreground">—</span>
                      {/if}
                    </Table.Cell>
                    <Table.Cell class="py-3">
                      <Badge
                        variant={meal.isAttributed ? "secondary" : "outline"}
                      >
                        {meal.isAttributed ? "Attributed" : "Unattributed"}
                      </Badge>
                    </Table.Cell>
                    <Table.Cell class="py-3">
                    </Table.Cell>
                  </Table.Row>

                  <!-- Suggested matches row (only for unattributed meals with suggestions) -->
                  {#if !meal.isAttributed && mealSuggestions.length > 0}
                    <Table.Row class="bg-primary/5 hover:bg-primary/10 border-l-2 border-l-primary">
                      <Table.Cell colspan={7} class="py-2 px-4">
                        <div class="space-y-2">
                          {#each mealSuggestions as match, matchIndex (`${match.foodEntryId}-${matchIndex}`)}
                            <div class="flex items-center justify-between gap-4">
                              <div class="flex items-center gap-3 min-w-0">
                                <Sparkles class="h-4 w-4 text-primary shrink-0" />
                                <div class="min-w-0">
                                  <span class="font-medium truncate">
                                    {match.foodName ?? match.mealName ?? "Food entry"}
                                  </span>
                                  <span class="text-sm text-muted-foreground ml-2">
                                    {match.carbs}g carbs
                                    · {Math.round((match.matchScore ?? 0) * 100)}% match
                                  </span>
                                </div>
                              </div>
                              <div class="flex items-center gap-2 shrink-0">
                                <Button
                                  type="button"
                                  variant="ghost"
                                  size="sm"
                                  onclick={(e) => {
                                    e.stopPropagation();
                                    handleDismiss(match);
                                  }}
                                >
                                  Dismiss
                                </Button>
                                <Button
                                  type="button"
                                  variant="outline"
                                  size="sm"
                                  onclick={(e) => {
                                    e.stopPropagation();
                                    openReviewDialog(match);
                                  }}
                                >
                                  Review
                                </Button>
                                <Button
                                  type="button"
                                  size="sm"
                                  onclick={(e) => {
                                    e.stopPropagation();
                                    handleQuickAccept(match);
                                  }}
                                >
                                  Accept
                                </Button>
                              </div>
                            </div>
                          {/each}
                        </div>
                      </Table.Cell>
                    </Table.Row>
                  {/if}

                  <!-- Expanded details row (only shown when there are foods) -->
                  {#if isExpanded && hasFoods}
                    <Table.Row class="bg-accent/20 hover:bg-accent/20">
                      <Table.Cell colspan={7} class="py-4">
                        <div class="space-y-4 px-4">
                          <!-- Food details -->
                          <div class="space-y-2">
                            <div class="text-sm font-medium">
                              Foods ({meal.foods?.length})
                            </div>
                            <div
                              class="grid gap-2 md:grid-cols-2 lg:grid-cols-3"
                            >
                              {#each meal.foods ?? [] as food}
                                <button
                                  type="button"
                                  onclick={() => openEditFoodEntry(meal, food)}
                                  class="rounded-lg border bg-card p-3 text-sm text-left hover:bg-accent/50 transition-colors cursor-pointer"
                                >
                                  <div class="font-medium">
                                    {food.foodName ?? food.note ?? "Other"}
                                  </div>
                                  <FoodEntryDetails
                                    {food}
                                    class="text-muted-foreground"
                                  />
                                </button>
                              {/each}
                            </div>
                          </div>

                          <!-- Quick stats -->
                          <div
                            class="flex flex-wrap gap-4 text-sm text-muted-foreground"
                          >
                            <span>
                              Attributed: {meal.attributedCarbs ?? 0}g
                            </span>
                            <span>
                              Unspecified: {meal.unspecifiedCarbs ?? 0}g
                            </span>
                          </div>
                        </div>
                      </Table.Cell>
                    </Table.Row>
                  {/if}
                {/each}
              {/if}
            {/each}
          </Table.Body>
        </Table.Root>
      {/if}
    </Card.Content>
  </Card.Root>
</div>

<TreatmentFoodSelectorDialog
  bind:open={showAddFoodDialog}
  onOpenChange={(value) => {
    showAddFoodDialog = value;
    if (!value) addFoodMeal = null;
  }}
  onSubmit={handleAddFoodSubmit}
  totalCarbs={addFoodMeal?.carbIntake?.carbs ?? 0}
  unspecifiedCarbs={addFoodMeal?.unspecifiedCarbs ??
    addFoodMeal?.carbIntake?.carbs ??
    0}
/>

<TreatmentFoodEntryEditDialog
  bind:open={showEditFoodEntryDialog}
  onOpenChange={(value) => {
    showEditFoodEntryDialog = value;
    if (!value) {
      editFoodEntry = null;
      editFoodEntryMeal = null;
    }
  }}
  entry={editFoodEntry}
  treatmentId={editFoodEntryMeal?.carbIntake?.id}
  totalCarbs={editFoodEntryMeal?.carbIntake?.carbs ?? 0}
  remainingCarbs={editFoodEntryMeal
    ? getRemainingCarbsForEntry(editFoodEntryMeal, editFoodEntry?.id)
    : 0}
  onSave={handleFoodEntrySaved}
/>

<MealMatchReviewDialog
  bind:open={showReviewDialog}
  onOpenChange={(value) => {
    showReviewDialog = value;
    if (!value) reviewMatch = null;
  }}
  match={reviewMatch}
  onComplete={handleReviewComplete}
/>
