<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import {
    Calendar,
    ChevronDown,
    ChevronRight,
    Loader2,
    Utensils,
    X,
  } from "lucide-svelte";
  import type { MealEvent, TreatmentFood, SuggestedMealMatch } from "$lib/api";
  import { cn } from "$lib/utils";
  import SortableColumnHeader from "./SortableColumnHeader.svelte";
  import MealSuggestionRow from "./MealSuggestionRow.svelte";
  import CarbBreakdownBar from "$lib/components/treatments/CarbBreakdownBar.svelte";
  import FoodEntryDetails from "$lib/components/treatments/FoodEntryDetails.svelte";
  import { getMealNameForTime } from "$lib/constants/meal-times";

  interface MealsByDay {
    date: string;
    displayDate: string;
    meals: MealEvent[];
  }

  interface Props {
    mealsByDay: MealsByDay[];
    sortColumn: string;
    sortDirection: "asc" | "desc";
    expandedRows: Set<string>;
    collapsedDates: Set<string>;
    isLoading: boolean;
    filteredAndSortedMealsCount: number;
    mealsCount: number;
    suggestionsByCarbIntake: Map<string, SuggestedMealMatch[]>;
    onSort: (column: string) => void;
    onToggleRow: (id: string) => void;
    onToggleDate: (date: string) => void;
    onAddFood: (meal: MealEvent) => void;
    onEditFood: (meal: MealEvent, food: TreatmentFood) => void;
    onUnlinkFood: (meal: MealEvent, food: TreatmentFood) => void;
    onEditInsulin: (meal: MealEvent) => void;
    onAcceptSuggestion: (suggestion: SuggestedMealMatch) => void;
    onDismissSuggestion: (suggestion: SuggestedMealMatch) => void;
    onReviewSuggestion: (suggestion: SuggestedMealMatch) => void;
  }

  let {
    mealsByDay,
    sortColumn,
    sortDirection,
    expandedRows,
    collapsedDates,
    isLoading,
    filteredAndSortedMealsCount,
    mealsCount,
    suggestionsByCarbIntake,
    onSort,
    onToggleRow,
    onToggleDate,
    onAddFood,
    onEditFood,
    onUnlinkFood,
    onEditInsulin,
    onAcceptSuggestion,
    onDismissSuggestion,
    onReviewSuggestion,
  }: Props = $props();

  function formatTime(mills: number | undefined): string {
    if (!mills) return "—";
    return new Date(mills).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  function getMealLabel(meal: MealEvent): string {
    const foods = meal.foods ?? [];
    if (foods.length === 0) {
      return "Meal";
    }
    if (foods.length === 1 && foods[0].foodName) {
      return foods[0].foodName;
    }
    const date = new Date(meal.carbIntakes?.[0]?.mills ?? Date.now());
    return getMealNameForTime(date);
  }

  function getFoodsSummary(foods: TreatmentFood[] | undefined): string {
    if (!foods || foods.length === 0) return "No foods attributed";
    return foods.map((f) => f.foodName ?? f.note ?? "Other").join(", ");
  }
</script>

<Card.Root>
  <Card.Content class="p-0">
    {#if isLoading}
      <div class="flex items-center justify-center p-12">
        <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
      </div>
    {:else if filteredAndSortedMealsCount === 0}
      <div class="p-6 text-center text-sm text-muted-foreground">
        {mealsCount === 0
          ? "No meals found in this range."
          : "No meals match the current filters."}
      </div>
    {:else}
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head class="w-12"></Table.Head>
            <Table.Head class="w-24">
              <SortableColumnHeader
                label="Time"
                column="time"
                {sortColumn}
                {sortDirection}
                onSort={() => onSort("time")}
              />
            </Table.Head>
            <Table.Head>
              <SortableColumnHeader
                label="Meal"
                column="meal"
                {sortColumn}
                {sortDirection}
                onSort={() => onSort("meal")}
              />
            </Table.Head>
            <Table.Head class="w-24 text-right">
              <SortableColumnHeader
                label="Carbs"
                column="carbs"
                {sortColumn}
                {sortDirection}
                onSort={() => onSort("carbs")}
              />
            </Table.Head>
            <Table.Head class="w-32 text-right">
              <SortableColumnHeader
                label="Insulin"
                column="insulin"
                {sortColumn}
                {sortDirection}
                onSort={() => onSort("insulin")}
              />
            </Table.Head>
            <Table.Head class="w-32">Status</Table.Head>
            <Table.Head class="w-24"></Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each mealsByDay as day}
            {@const isDateCollapsed = collapsedDates.has(day.date)}
            {@const dayTotalCarbs = day.meals.reduce(
              (sum, m) => sum + (m.totalCarbs ?? 0),
              0
            )}
            {@const dayTotalInsulin = day.meals.reduce(
              (sum, m) => sum + (m.totalInsulin ?? 0),
              0
            )}
            <!-- Day separator row -->
            <Table.Row
              class="bg-muted/50 hover:bg-muted/60 cursor-pointer transition-colors"
              onclick={() => onToggleDate(day.date)}
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
              {#each day.meals as meal, mealIndex (`${day.date}-${mealIndex}-${meal.carbIntakes?.[0]?.id}`)}
                {@const isExpanded = expandedRows.has(
                  meal.carbIntakes?.[0]?.id ?? ""
                )}
                {@const hasFoods = (meal.foods?.length ?? 0) > 0}
                {@const totalCarbs = meal.totalCarbs ?? 0}
                {@const mealSuggestions = suggestionsByCarbIntake.get(meal.carbIntakes?.[0]?.id ?? "") ?? []}

                <!-- Main meal row -->
                <Table.Row
                  class={cn(
                    "transition-colors",
                    hasFoods && "cursor-pointer",
                    isExpanded && "bg-accent/30"
                  )}
                  onclick={() =>
                    hasFoods && onToggleRow(meal.carbIntakes?.[0]?.id ?? "")}
                >
                  <Table.Cell class="py-3">
                    {#if hasFoods}
                      <Button
                        variant="ghost"
                        size="icon"
                        class="h-6 w-6"
                        onclick={(e: MouseEvent) => {
                          e.stopPropagation();
                          onToggleRow(meal.carbIntakes?.[0]?.id ?? "");
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
                      {formatTime(meal.carbIntakes?.[0]?.mills)}
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
                          onAddFood(meal);
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
                    <button
                      type="button"
                      class="cursor-pointer hover:opacity-80 transition-opacity"
                      onclick={(e) => {
                        e.stopPropagation();
                        onEditInsulin(meal);
                      }}
                    >
                      {#if meal.totalInsulin}
                        <span class="font-medium tabular-nums">
                          {meal.totalInsulin.toFixed(1)}U
                        </span>
                      {:else}
                        <span class="text-muted-foreground">—</span>
                      {/if}
                    </button>
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
                          <MealSuggestionRow
                            suggestion={match}
                            onAccept={onAcceptSuggestion}
                            onDismiss={onDismissSuggestion}
                            onReview={onReviewSuggestion}
                          />
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
                              <div class="rounded-lg border bg-card p-3 text-sm transition-colors group relative hover:bg-accent/50">
                                <button
                                  type="button"
                                  onclick={() => onEditFood(meal, food)}
                                  class="w-full text-left cursor-pointer"
                                >
                                  <div class="font-medium pr-6">
                                    {food.foodName ?? food.note ?? "Other"}
                                  </div>
                                  <FoodEntryDetails
                                    {food}
                                    class="text-muted-foreground"
                                  />
                                </button>
                                <button
                                  type="button"
                                  onclick={(e) => {
                                    e.stopPropagation();
                                    onUnlinkFood(meal, food);
                                  }}
                                  class="absolute top-2 right-2 p-1 rounded-md opacity-0 group-hover:opacity-100 hover:bg-destructive/10 hover:text-destructive transition-opacity"
                                  title="Unlink food"
                                >
                                  <X class="h-3.5 w-3.5" />
                                </button>
                              </div>
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
