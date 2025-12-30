<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import {
    Table,
    TableBody,
    TableCell,
    TableHead,
    TableHeader,
    TableRow,
  } from "$lib/components/ui/table";
  import { Edit, Trash2 } from "lucide-svelte";
  import { getFoodState } from "./food-context.js";
  import FoodFilters from "./FoodFilters.svelte";
  import type { FoodRecord } from "./types";
  import { getGiLabel } from "$lib/components/food";

  interface Props {
    handleFoodDragStart: (event: DragEvent, food: FoodRecord) => void;
    handleFoodDragEnd: (event: DragEvent) => void;
  }

  let { handleFoodDragStart, handleFoodDragEnd }: Props = $props();

  const foodStore = getFoodState();
</script>

<Card>
  <CardHeader>
    <CardTitle>Your database</CardTitle>
  </CardHeader>
  <CardContent class="space-y-4">
    <!-- Filters -->
    <FoodFilters />
    <!-- Food List -->
    <div class="border rounded-lg max-h-64 overflow-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead class="w-20">Actions</TableHead>
            <TableHead>Name</TableHead>
            <TableHead class="text-center font-semibold">Carbs</TableHead>
            <TableHead class="text-center font-semibold">GI</TableHead>
            <TableHead class="text-center">Portion</TableHead>
            <TableHead>Category</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {#each foodStore.filteredFoodList as food}
            <TableRow
              class="draggable-food cursor-grab"
              role="button"
              tabindex={0}
              draggable="true"
              ondragstart={(e) => handleFoodDragStart(e, food)}
              ondragend={handleFoodDragEnd}
            >
              <TableCell>
                <div class="flex gap-1">
                  <Button
                    variant="ghost"
                    size="sm"
                    onclick={() => foodStore.editFood(food)}
                    title="Edit food"
                  >
                    <Edit class="h-3 w-3" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    onclick={() => foodStore.deleteFood(food)}
                    title="Delete food"
                  >
                    <Trash2 class="h-3 w-3" />
                  </Button>
                </div>
              </TableCell>
              <TableCell class="truncate max-w-[200px]" title={food.name}
                >{food.name}</TableCell
              >
              <TableCell class="text-center font-medium">{food.carbs}g</TableCell
              >
              <TableCell class="text-center">{getGiLabel(food.gi)}</TableCell>
              <TableCell class="text-center text-muted-foreground">
                {food.portion}
                {food.unit}
              </TableCell>
              <TableCell class="truncate max-w-[150px] text-muted-foreground">
                {#if food.category}
                  {food.category}{#if food.subcategory} / {food.subcategory}{/if}
                {:else}
                  —
                {/if}
              </TableCell>
            </TableRow>
          {/each}
        </TableBody>
      </Table>
    </div>
  </CardContent>
</Card>
