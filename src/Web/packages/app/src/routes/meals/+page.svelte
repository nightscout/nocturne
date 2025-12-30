<script lang="ts">
  import DateRangePicker from "$lib/components/ui/date-range-picker.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Calendar, ChevronDown, ChevronRight, Plus } from "lucide-svelte";
  import type {
    MealTreatment,
    Treatment,
    TreatmentFood,
    TreatmentFoodRequest,
  } from "$lib/api";
  import { getMealTreatments } from "$lib/data/treatment-foods.remote";
  import {
    updateTreatment,
    deleteTreatment,
  } from "$lib/data/treatments.remote";
  import { toast } from "svelte-sonner";
  import { invalidateAll } from "$app/navigation";
  import {
    TreatmentEditDialog,
    TreatmentFoodSelectorDialog,
    TreatmentFoodEntryEditDialog,
    CarbBreakdownBar,
  } from "$lib/components/treatments";
  import { addTreatmentFood } from "$lib/data/treatment-foods.remote";
  import { TreatmentTypeIcon } from "$lib/components/icons";
  import { getMealNameForTime } from "$lib/constants/meal-times";
  import { cn } from "$lib/utils";

  let dateRange = $state<{ from?: string; to?: string }>({});
  let filterMode = $state<"all" | "unattributed">("all");

  let showEditDialog = $state(false);
  let treatmentToEdit = $state<Treatment | null>(null);
  let isSaving = $state(false);
  let expandedRows = $state<Set<string>>(new Set());
  let collapsedDates = $state<Set<string>>(new Set());

  // Add food dialog state
  let showAddFoodDialog = $state(false);
  let addFoodMeal = $state<MealTreatment | null>(null);

  // Edit food entry dialog state
  let showEditFoodEntryDialog = $state(false);
  let editFoodEntry = $state<TreatmentFood | null>(null);
  let editFoodEntryMeal = $state<MealTreatment | null>(null);

  function handleDateChange(params: { from?: string; to?: string }) {
    dateRange = { from: params.from, to: params.to };
  }

  const queryParams = $derived({
    from: dateRange.from,
    to: dateRange.to,
    attributed: filterMode === "unattributed" ? false : undefined,
  });

  const mealsQuery = $derived(getMealTreatments(queryParams));
  const meals = $derived<MealTreatment[]>(mealsQuery.current ?? []);

  // Group meals by date for day separators
  interface MealsByDay {
    date: string;
    displayDate: string;
    meals: MealTreatment[];
  }

  const mealsByDay = $derived.by(() => {
    const grouped = new Map<string, MealTreatment[]>();

    for (const meal of meals) {
      const dateStr = meal.treatment?.created_at;
      if (!dateStr) continue;

      const date = new Date(dateStr);
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
          dayMeals[0].treatment?.created_at!
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

  function openEdit(treatment: Treatment) {
    treatmentToEdit = treatment;
    showEditDialog = true;
  }

  function openAddFood(meal: MealTreatment) {
    addFoodMeal = meal;
    showAddFoodDialog = true;
  }

  function openEditFoodEntry(meal: MealTreatment, food: TreatmentFood) {
    editFoodEntryMeal = meal;
    editFoodEntry = food;
    showEditFoodEntryDialog = true;
  }

  function getRemainingCarbsForEntry(
    meal: MealTreatment,
    entryId: string | undefined
  ): number {
    const totalCarbs = meal.treatment?.carbs ?? 0;
    const otherAttributedCarbs =
      meal.foods
        ?.filter((f) => f.id !== entryId)
        .reduce((sum, f) => sum + (f.carbs ?? 0), 0) ?? 0;
    return Math.round((totalCarbs - otherAttributedCarbs) * 10) / 10;
  }

  async function handleFoodEntrySaved() {
    await getMealTreatments(queryParams).refresh();
  }

  async function handleAddFoodSubmit(request: TreatmentFoodRequest) {
    if (!addFoodMeal?.treatment?._id) return;

    try {
      await addTreatmentFood({
        treatmentId: addFoodMeal.treatment._id,
        request,
      });
      toast.success("Food added");
      showAddFoodDialog = false;
      addFoodMeal = null;
      getMealTreatments(queryParams).refresh();
    } catch (err) {
      console.error("Add food error:", err);
      toast.error("Failed to add food");
    }
  }

  function handleEditClose() {
    showEditDialog = false;
    treatmentToEdit = null;
  }

  async function handleEditSave(updatedTreatment: Treatment) {
    isSaving = true;
    try {
      await updateTreatment({ ...updatedTreatment });
      toast.success("Treatment updated");
      showEditDialog = false;
      treatmentToEdit = null;
      getMealTreatments(queryParams).refresh();
      invalidateAll();
    } catch (err) {
      console.error("Update error:", err);
      toast.error("Failed to update treatment");
    } finally {
      isSaving = false;
    }
  }

  async function handleEditDelete(treatmentId: string) {
    isSaving = true;
    try {
      await deleteTreatment(treatmentId);
      toast.success("Treatment deleted");
      showEditDialog = false;
      treatmentToEdit = null;
      getMealTreatments(queryParams).refresh();
      invalidateAll();
    } catch (err) {
      console.error("Delete error:", err);
      toast.error("Failed to delete treatment");
    } finally {
      isSaving = false;
    }
  }

  function formatTime(dateStr: string | undefined): string {
    if (!dateStr) return "—";
    return new Date(dateStr).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  function getMealLabel(meal: MealTreatment): string {
    const foods = meal.foods ?? [];
    if (foods.length === 0) {
      return meal.treatment?.eventType ?? "Meal";
    }
    if (foods.length === 1 && foods[0].foodName) {
      return foods[0].foodName;
    }
    // Multiple foods - use meal time-based name
    const date = new Date(meal.treatment?.created_at ?? new Date());
    return getMealNameForTime(date);
  }

  function getFoodsSummary(foods: TreatmentFood[] | undefined): string {
    if (!foods || foods.length === 0) return "No foods attributed";
    return foods.map((f) => f.foodName ?? f.note ?? "Other").join(", ");
  }
</script>

<svelte:head>
  <title>Meals - Nocturne</title>
  <meta
    name="description"
    content="Review carb treatments and add food breakdowns for better meal documentation"
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
      Pair carb treatments with foods when you want more detail.
    </p>
  </div>

  <DateRangePicker
    title="Meal range"
    defaultDays={1}
    onDateChange={handleDateChange}
  />

  <div class="flex items-center gap-2">
    <Button
      type="button"
      variant={filterMode === "all" ? "default" : "outline"}
      onclick={() => (filterMode = "all")}
    >
      All
    </Button>
    <Button
      type="button"
      variant={filterMode === "unattributed" ? "default" : "outline"}
      onclick={() => (filterMode = "unattributed")}
    >
      Unattributed only
    </Button>
  </div>

  <Card.Root>
    <Card.Content class="p-0">
      {#if meals.length === 0}
        <div class="p-6 text-center text-sm text-muted-foreground">
          No meals found in this range.
        </div>
      {:else}
        <Table.Root>
          <Table.Header>
            <Table.Row>
              <Table.Head class="w-12"></Table.Head>
              <Table.Head class="w-24">Time</Table.Head>
              <Table.Head>Meal</Table.Head>
              <Table.Head class="w-24 text-right">Carbs</Table.Head>
              <Table.Head class="w-32 text-right">Insulin</Table.Head>
              <Table.Head class="w-32">Status</Table.Head>
              <Table.Head class="w-24"></Table.Head>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {#each mealsByDay as day}
              {@const isDateCollapsed = collapsedDates.has(day.date)}
              {@const dayTotalCarbs = day.meals.reduce(
                (sum, m) => sum + (m.treatment?.carbs ?? 0),
                0
              )}
              {@const dayTotalInsulin = day.meals.reduce(
                (sum, m) => sum + (m.treatment?.insulin ?? 0),
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
                {#each day.meals as meal (meal.treatment?._id)}
                  {@const isExpanded = expandedRows.has(
                    meal.treatment?._id ?? ""
                  )}
                  {@const hasFoods = (meal.foods?.length ?? 0) > 0}
                  {@const totalCarbs = meal.treatment?.carbs ?? 0}

                  <!-- Main meal row -->
                  <Table.Row
                    class={cn(
                      "transition-colors",
                      hasFoods && "cursor-pointer",
                      isExpanded && "bg-accent/30"
                    )}
                    onclick={() =>
                      hasFoods && toggleRow(meal.treatment?._id ?? "")}
                  >
                    <Table.Cell class="py-3">
                      {#if hasFoods}
                        <Button
                          variant="ghost"
                          size="icon"
                          class="h-6 w-6"
                          onclick={(e) => {
                            e.stopPropagation();
                            toggleRow(meal.treatment?._id ?? "");
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
                        {formatTime(meal.treatment?.created_at)}
                      </div>
                    </Table.Cell>
                    <Table.Cell class="py-3">
                      <div class="flex items-center gap-2">
                        <TreatmentTypeIcon
                          eventType={meal.treatment?.eventType}
                          class="h-4 w-4"
                        />
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
                      {#if meal.treatment?.insulin}
                        <span class="font-medium tabular-nums">
                          {meal.treatment.insulin.toFixed(1)}U
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
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        onclick={(e) => {
                          e.stopPropagation();
                          meal.treatment && openEdit(meal.treatment);
                        }}
                      >
                        Edit
                      </Button>
                    </Table.Cell>
                  </Table.Row>

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
                                  <div class="text-muted-foreground">
                                    {food.portions ?? 1} portion{(food.portions ??
                                      1) !== 1
                                      ? "s"
                                      : ""} • {food.carbs}g carbs
                                    {#if food.timeOffsetMinutes}
                                      • +{food.timeOffsetMinutes} min
                                    {/if}
                                  </div>
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
                            {#if meal.treatment?.notes}
                              <span>Notes: {meal.treatment.notes}</span>
                            {/if}
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

<TreatmentEditDialog
  bind:open={showEditDialog}
  treatment={treatmentToEdit}
  availableEventTypes={[]}
  isLoading={isSaving}
  onClose={handleEditClose}
  onSave={handleEditSave}
  onDelete={handleEditDelete}
/>

<TreatmentFoodSelectorDialog
  bind:open={showAddFoodDialog}
  onOpenChange={(value) => {
    showAddFoodDialog = value;
    if (!value) addFoodMeal = null;
  }}
  onSubmit={handleAddFoodSubmit}
  totalCarbs={addFoodMeal?.treatment?.carbs ?? 0}
  unspecifiedCarbs={addFoodMeal?.unspecifiedCarbs ??
    addFoodMeal?.treatment?.carbs ??
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
  treatmentId={editFoodEntryMeal?.treatment?._id}
  totalCarbs={editFoodEntryMeal?.treatment?.carbs ?? 0}
  remainingCarbs={editFoodEntryMeal
    ? getRemainingCarbsForEntry(editFoodEntryMeal, editFoodEntry?.id)
    : 0}
  onSave={handleFoodEntrySaved}
/>
