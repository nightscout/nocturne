<script lang="ts">
  import type { CarbIntake, CarbIntakeFoodRequest } from "$lib/api";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Apple, X, ChevronDown, Plus, Trash2 } from "lucide-svelte";
  import FoodBreakdown from "./FoodBreakdown.svelte";
  import { TreatmentFoodSelectorDialog } from "$lib/components/treatments";

  export interface PendingFood {
    request: CarbIntakeFoodRequest;
    displayName: string;
    displayCarbs: number;
  }

  interface Props {
    carbIntake: Partial<CarbIntake>;
    /** Set when editing an existing CarbIntake — enables food breakdown */
    carbIntakeId?: string;
    /** Pending foods to add when creating a new carb intake */
    pendingFoods?: PendingFood[];
    onRemove?: () => void;
  }

  let {
    carbIntake = $bindable(),
    carbIntakeId,
    pendingFoods = $bindable([]),
    onRemove,
  }: Props = $props();

  let showAdvanced = $state(false);
  let showAddFood = $state(false);

  const pendingTotalCarbs = $derived(
    pendingFoods.reduce((sum, f) => sum + f.displayCarbs, 0),
  );

  const unspecifiedCarbs = $derived(
    Math.max(0, (carbIntake.carbs ?? 0) - pendingTotalCarbs),
  );

  function handleAddPendingFood(request: CarbIntakeFoodRequest, displayName?: string) {
    const name = displayName ?? request.note ?? "Food";
    const carbs = request.carbs ?? 0;

    pendingFoods = [
      ...pendingFoods,
      { request, displayName: name, displayCarbs: carbs },
    ];
    showAddFood = false;
  }

  function removePendingFood(index: number) {
    pendingFoods = pendingFoods.filter((_, i) => i !== index);
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Apple class="h-4 w-4 text-green-500" />
      Carb Intake
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="grid grid-cols-2 gap-3">
    <div class="space-y-1.5">
      <Label for="carb-carbs{carbIntakeId ?? ''}">Carbs (g)</Label>
      <Input
        id="carb-carbs{carbIntakeId ?? ''}"
        type="number"
        step="1"
        min="0"
        bind:value={carbIntake.carbs}
        placeholder="0"
      />
    </div>

    <div class="space-y-1.5">
      <Label for="carb-food-type{carbIntakeId ?? ''}">Food Type</Label>
      <Input
        id="carb-food-type{carbIntakeId ?? ''}"
        type="text"
        bind:value={carbIntake.foodType}
        placeholder="e.g. Sandwich"
      />
    </div>
  </div>

  <Collapsible.Root bind:open={showAdvanced}>
    <Collapsible.Trigger
      class="flex items-center gap-1 px-2 h-7 text-xs text-muted-foreground hover:text-foreground transition-colors"
    >
      <ChevronDown class="h-3 w-3 transition-transform {showAdvanced ? 'rotate-180' : ''}" />
      Advanced
    </Collapsible.Trigger>
    <Collapsible.Content>
      <div class="grid grid-cols-3 gap-3 pt-2">
        <div class="space-y-1.5">
          <Label for="carb-protein{carbIntakeId ?? ''}">Protein (g)</Label>
          <Input
            id="carb-protein{carbIntakeId ?? ''}"
            type="number"
            step="1"
            min="0"
            bind:value={carbIntake.protein}
            placeholder="0"
          />
        </div>

        <div class="space-y-1.5">
          <Label for="carb-fat{carbIntakeId ?? ''}">Fat (g)</Label>
          <Input
            id="carb-fat{carbIntakeId ?? ''}"
            type="number"
            step="1"
            min="0"
            bind:value={carbIntake.fat}
            placeholder="0"
          />
        </div>

        <div class="space-y-1.5">
          <Label for="carb-absorption{carbIntakeId ?? ''}">Absorption (min)</Label>
          <Input
            id="carb-absorption{carbIntakeId ?? ''}"
            type="number"
            step="1"
            min="0"
            bind:value={carbIntake.absorptionTime}
            placeholder="0"
          />
        </div>
      </div>
    </Collapsible.Content>
  </Collapsible.Root>

  {#if carbIntakeId}
    <FoodBreakdown {carbIntakeId} totalCarbs={carbIntake.carbs ?? 0} />
  {:else}
    <!-- Pending foods for new carb intakes -->
    <div class="rounded-lg border p-4 space-y-3">
      <div class="flex items-center justify-between">
        <div class="space-y-1">
          <div class="text-sm font-semibold">Foods</div>
          <div class="text-xs text-muted-foreground">
            Link foods to this carb intake.
          </div>
        </div>
        <Button type="button" size="sm" onclick={() => (showAddFood = true)}>
          <Plus class="mr-1 h-4 w-4" />
          Add Food
        </Button>
      </div>

      {#if pendingFoods.length === 0}
        <div
          class="rounded-md border border-dashed p-4 text-sm text-muted-foreground"
        >
          No foods added yet.
        </div>
      {:else}
        <div class="space-y-2">
          {#each pendingFoods as food, index (index)}
            <div class="flex items-center justify-between rounded-md border p-3">
              <div class="space-y-0.5">
                <div class="text-sm font-medium">{food.displayName}</div>
                <div class="text-xs text-muted-foreground">
                  {food.displayCarbs}g carbs
                </div>
              </div>
              <Button
                type="button"
                variant="ghost"
                size="icon"
                class="h-7 w-7 text-destructive"
                onclick={() => removePendingFood(index)}
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            </div>
          {/each}
        </div>

        {#if (carbIntake.carbs ?? 0) > 0}
          <div class="flex flex-wrap gap-2 text-xs">
            <Badge variant="secondary">
              Attributed {pendingTotalCarbs}g
            </Badge>
            <Badge variant="outline">
              Unspecified {unspecifiedCarbs}g
            </Badge>
          </div>
        {/if}
      {/if}
    </div>

    <TreatmentFoodSelectorDialog
      bind:open={showAddFood}
      onOpenChange={(value) => (showAddFood = value)}
      onSubmit={handleAddPendingFood}
      totalCarbs={carbIntake.carbs ?? 0}
      {unspecifiedCarbs}
    />
  {/if}
</div>
