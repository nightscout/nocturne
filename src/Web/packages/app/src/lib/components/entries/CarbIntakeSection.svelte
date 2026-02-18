<script lang="ts">
  import type { CarbIntake } from "$lib/api";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Apple, X, ChevronDown } from "lucide-svelte";
  import FoodBreakdown from "./FoodBreakdown.svelte";

  interface Props {
    carbIntake: Partial<CarbIntake>;
    /** Set when editing an existing CarbIntake — enables food breakdown */
    carbIntakeId?: string;
    onRemove?: () => void;
  }

  let { carbIntake = $bindable(), carbIntakeId, onRemove }: Props = $props();

  let showAdvanced = $state(false);
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
  {/if}
</div>
