<script lang="ts">
  import { startOfDay, toDayString } from "$lib/utils/date-range";
  import { formatLongDate } from "$lib/utils/formatting";
  import { Calendar } from "lucide-svelte";
  import type {
    MealEvent,
    TreatmentFood,
    CarbIntakeFoodRequest,
    SuggestedMealMatch,
  } from "$lib/api";
  import { getMeals, addCarbIntakeFood, deleteCarbIntakeFood } from "$api/generated/nutritions.generated.remote";
  import { getSuggestions as getMealMatchingSuggestions, acceptMatch, dismissMatch } from "$api/generated/mealMatchings.generated.remote";
  import { toast } from "svelte-sonner";
  import { useToastSubmission } from "$lib/forms";
  import {
    TreatmentFoodSelectorDialog,
    TreatmentFoodEntryEditDialog,
  } from "$lib/components/treatments";
  import { getMealNameForTime } from "$lib/constants/meal-times";
  import { MealMatchReviewDialog } from "$lib/components/meal-matching";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import MealsFilterBar from "$lib/components/meals/MealsFilterBar.svelte";
  import MealsTable from "$lib/components/meals/MealsTable.svelte";
  import MealBolusDialog from "$lib/components/meals/MealBolusDialog.svelte";
  import { coachmark } from "@nocturne/coach";
  import { localDayStart, localDayEnd } from "$lib/utils/timezone";
  import { Now } from "$lib/hooks/now.svelte";

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

  let expandedRows = $state<Set<string>>(new Set());
  let collapsedDates = $state<Set<string>>(new Set());

  // Add food dialog state
  let showAddFoodDialog = $state(false);
  let addFoodMeal = $state<MealEvent | null>(null);

  // Edit food entry dialog state
  let showEditFoodEntryDialog = $state(false);
  let editFoodEntry = $state<TreatmentFood | null>(null);
  let editFoodEntryMeal = $state<MealEvent | null>(null);

  // Meal match review dialog state
  let showReviewDialog = $state(false);
  let reviewMatch = $state<SuggestedMealMatch | null>(null);

  // Bolus dialog state
  let showBolusDialog = $state(false);
  let bolusMeal = $state<MealEvent | null>(null);

  // Unlink food confirmation state
  let showUnlinkConfirm = $state(false);
  let unlinkTarget = $state<{ meal: MealEvent; food: TreatmentFood } | null>(null);
  const unlink = useToastSubmission("Failed to unlink food");
  const addFood = useToastSubmission("Failed to add food");

  const queryParams = $derived({
    from: dateRange.from ? localDayStart(dateRange.from).getTime() : undefined,
    to: dateRange.to ? localDayEnd(dateRange.to).getTime() : undefined,
    attributed: filterMode === "unattributed" ? false : undefined,
  });

  const mealsQuery = $derived(getMeals(queryParams));
  const meals = $derived<MealEvent[]>(mealsQuery.current ?? []);

  // Query for suggested meal matches using the endpoint. `now` keeps the
  // unfiltered default on today's date for a page left open past midnight, and
  // uses the viewer's local date rather than UTC.
  const now = new Now();
  const suggestionsQueryParams = $derived({
    from: dateRange.from ?? now.localDate,
    to: dateRange.to ?? now.localDate,
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
      const carbIntakeId = match.carbIntakeId;
      if (!carbIntakeId) continue;
      if (!map.has(carbIntakeId)) {
        map.set(carbIntakeId, []);
      }
      map.get(carbIntakeId)!.push(match);
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


  // Helper to get meal label for sorting
  function getMealSortLabel(meal: MealEvent): string {
    const foods = meal.foods ?? [];
    if (foods.length === 0) return "Meal";
    if (foods.length === 1 && foods[0].foodName) return foods[0].foodName;
    return getMealNameForTime(
      new Date(meal.carbIntakes?.[0]?.mills ?? Date.now())
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
            (a.carbIntakes?.[0]?.mills ?? 0) - (b.carbIntakes?.[0]?.mills ?? 0);
          break;
        case "meal":
          comparison = getMealSortLabel(a).localeCompare(getMealSortLabel(b));
          break;
        case "carbs":
          comparison =
            (a.totalCarbs ?? 0) - (b.totalCarbs ?? 0);
          break;
        case "insulin":
          comparison =
            (a.totalInsulin ?? 0) -
            (b.totalInsulin ?? 0);
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
    meals: MealEvent[];
  }

  const mealsByDay = $derived.by(() => {
    const grouped = new Map<string, MealEvent[]>();

    for (const meal of filteredAndSortedMeals) {
      const mills = meal.carbIntakes?.[0]?.mills;
      if (!mills) continue;

      const dateKey = toDayString(new Date(mills));

      if (!grouped.has(dateKey)) {
        grouped.set(dateKey, []);
      }
      grouped.get(dateKey)!.push(meal);
    }

    const result: MealsByDay[] = [];
    for (const [date, dayMeals] of grouped) {
      result.push({
        date,
        displayDate: formatLongDate(startOfDay(date)),
        meals: dayMeals,
      });
    }

    return result;
  });

  // Sorting helper
  function toggleSort(column: string) {
    const col = column as SortColumn;
    if (sortColumn === col) {
      sortDirection = sortDirection === "asc" ? "desc" : "asc";
    } else {
      sortColumn = col;
      sortDirection = col === "time" ? "desc" : "asc";
    }
  }

  function clearAllFilters() {
    searchQuery = "";
    selectedFoods = [];
  }

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

  function openAddFood(meal: MealEvent) {
    addFoodMeal = meal;
    showAddFoodDialog = true;
  }

  function openBolusDialog(meal: MealEvent) {
    bolusMeal = meal;
    showBolusDialog = true;
  }

  function openEditFoodEntry(meal: MealEvent, food: TreatmentFood) {
    editFoodEntryMeal = meal;
    editFoodEntry = food;
    showEditFoodEntryDialog = true;
  }

  function getRemainingCarbsForEntry(
    meal: MealEvent,
    entryId: string | undefined
  ): number {
    const totalCarbs = meal.totalCarbs ?? 0;
    const otherAttributedCarbs =
      meal.foods
        ?.filter((f: TreatmentFood) => f.id !== entryId)
        .reduce((sum: number, f: TreatmentFood) => sum + (f.carbs ?? 0), 0) ?? 0;
    return Math.round((totalCarbs - otherAttributedCarbs) * 10) / 10;
  }

  function confirmUnlinkFood(meal: MealEvent, food: TreatmentFood) {
    unlinkTarget = { meal, food };
    showUnlinkConfirm = true;
  }

  async function handleUnlinkFood() {
    if (!unlinkTarget) return;
    const { meal, food } = unlinkTarget;
    const carbIntakeId = meal.carbIntakes?.[0]?.id;
    const foodEntryId = food.id;
    if (!carbIntakeId || !foodEntryId) return;

    await unlink.run(async () => {
      await deleteCarbIntakeFood({
        id: carbIntakeId,
        foodEntryId,
      });
      toast.success("Food unlinked");
      showUnlinkConfirm = false;
      unlinkTarget = null;
      mealsQuery.refresh();
    });
  }

  async function handleFoodEntrySaved() {
    await mealsQuery.refresh();
  }

  async function handleAddFoodSubmit(request: CarbIntakeFoodRequest) {
    const carbIntakeId = addFoodMeal?.carbIntakes?.[0]?.id;
    if (!carbIntakeId) return;

    await addFood.run(async () => {
      await addCarbIntakeFood({
        id: carbIntakeId,
        request,
      });
      toast.success("Food added");
      showAddFoodDialog = false;
      addFoodMeal = null;
      mealsQuery.refresh();
    });
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
        carbIntakeId: match.carbIntakeId!,
        carbs: match.carbs ?? 0,
        timeOffsetMinutes: 0,
      });
      toast.success("Meal match accepted");
      // Awaited so the row's pending state covers the refresh too, not just the
      // command; otherwise the row is clickable again before it disappears.
      await Promise.all([mealsQuery.refresh(), suggestionsQuery.refresh()]);
    } catch (err) {
      console.error("Failed to accept match:", err);
      toast.error("Failed to accept match");
    }
  }

  async function handleDismiss(match: SuggestedMealMatch) {
    try {
      await dismissMatch({ foodEntryId: match.foodEntryId! });
      toast.success("Match dismissed");
      await suggestionsQuery.refresh();
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

  <div>
    <MealsFilterBar
      bind:dateRange
      bind:filterMode
      bind:searchQuery
      bind:selectedFoods
      {uniqueFoods}
      onClearFilters={clearAllFilters}
    />
  </div>

  <div {@attach coachmark({
    key: "feature-intro.meals-matching",
    title: "Smart matching",
    description: "Nocturne suggests food matches based on your history \u2014 review them here.",
    completeOn: { event: "click" },
  })}>
    <MealsTable
      {mealsByDay}
      {sortColumn}
      {sortDirection}
      {expandedRows}
      {collapsedDates}
      {isLoading}
      filteredAndSortedMealsCount={filteredAndSortedMeals.length}
      mealsCount={meals.length}
      {suggestionsByCarbIntake}
      onSort={toggleSort}
      onToggleRow={toggleRow}
      onToggleDate={toggleDate}
      onAddFood={openAddFood}
      onEditFood={openEditFoodEntry}
      onUnlinkFood={confirmUnlinkFood}
      onEditInsulin={openBolusDialog}
      onAcceptSuggestion={handleQuickAccept}
      onDismissSuggestion={handleDismiss}
      onReviewSuggestion={openReviewDialog}
    />
  </div>
</div>

<TreatmentFoodSelectorDialog
  bind:open={showAddFoodDialog}
  onOpenChange={(value) => {
    showAddFoodDialog = value;
    if (!value) addFoodMeal = null;
  }}
  onSubmit={handleAddFoodSubmit}
  submitting={addFood.busy}
  totalCarbs={addFoodMeal?.totalCarbs ?? 0}
  unspecifiedCarbs={addFoodMeal?.unspecifiedCarbs ??
    addFoodMeal?.totalCarbs ??
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
  carbIntakeId={editFoodEntryMeal?.carbIntakes?.[0]?.id}
  totalCarbs={editFoodEntryMeal?.totalCarbs ?? 0}
  remainingCarbs={editFoodEntryMeal
    ? getRemainingCarbsForEntry(editFoodEntryMeal, editFoodEntry?.id)
    : 0}
  onSave={handleFoodEntrySaved}
/>

<MealBolusDialog
  bind:open={showBolusDialog}
  onOpenChange={(value) => {
    showBolusDialog = value;
    if (!value) bolusMeal = null;
  }}
  meal={bolusMeal}
  onSave={() => mealsQuery.refresh()}
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

<ConfirmDialog
  bind:open={showUnlinkConfirm}
  onOpenChange={(o) => { if (!o) unlinkTarget = null; }}
  title="Unlink food"
  confirmLabel={unlink.busy ? "Removing..." : "Remove"}
  destructive
  busy={unlink.busy}
  onConfirm={handleUnlinkFood}
>
  {#snippet description()}
    Remove "{unlinkTarget?.food.foodName ?? unlinkTarget?.food.note ?? 'this food'}" from this meal? The food will remain in your database.
  {/snippet}
</ConfirmDialog>
