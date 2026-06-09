<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Scale } from "lucide-svelte";

  interface Props {
    portions: number | null;
    entryCarbs: number | null;
    timeOffsetMinutes: number;
    note: string;
    foodCarbs: number | null;
    totalCarbs?: number;
    unspecifiedCarbs?: number;
    showPortions: boolean;
    onScaleToFit: () => void;
    onPortionsChange?: (value: number) => void;
    onCarbsChange?: (value: number) => void;
  }

  let {
    portions = $bindable(),
    entryCarbs = $bindable(),
    timeOffsetMinutes = $bindable(),
    note = $bindable(),
    foodCarbs,
    totalCarbs = 0,
    unspecifiedCarbs = 0,
    showPortions,
    onScaleToFit,
    onPortionsChange,
    onCarbsChange,
  }: Props = $props();
</script>

<div class="@container border-t pt-4 space-y-4">
  <div class="text-sm font-medium">How much did you eat?</div>

  <!-- Show remaining carbs context -->
  {#if totalCarbs > 0}
    <div
      class="flex items-center justify-between rounded-md bg-muted/50 px-3 py-2 text-sm"
    >
      <span>Remaining to attribute</span>
      <span class="font-semibold tabular-nums">
        {unspecifiedCarbs}g
      </span>
    </div>
  {/if}

  <!-- Bidirectional portion/carbs input -->
  <div class="grid gap-4 @lg:grid-cols-3">
    {#if showPortions}
      <div class="space-y-2">
        <Label for="portions">Portions</Label>
        <Input
          id="portions"
          type="number"
          step="0.1"
          min="0"
          value={portions}
          oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
            onPortionsChange?.(parseFloat(e.currentTarget.value) || 0)}
        />
      </div>
    {/if}
    <div class="space-y-2">
      <Label for="entry-carbs">Carbs (g)</Label>
      <Input
        id="entry-carbs"
        type="number"
        step="0.1"
        min="0"
        value={entryCarbs}
        oninput={(e: Event & { currentTarget: HTMLInputElement }) =>
          onCarbsChange?.(parseFloat(e.currentTarget.value) || 0)}
      />
    </div>
    <div class="space-y-2">
      <Label for="offset">Time offset (min)</Label>
      <Input
        id="offset"
        type="number"
        step="1"
        bind:value={timeOffsetMinutes}
      />
    </div>
  </div>

  <!-- Scale to fit button - show for food selection (needs foodCarbs) or log without saving -->
  {#if unspecifiedCarbs > 0 && ((foodCarbs ?? 0) > 0 || !showPortions)}
    <Button
      type="button"
      variant="outline"
      size="sm"
      class="w-full"
      onclick={onScaleToFit}
    >
      <Scale class="mr-2 h-4 w-4" />
      Scale to fill remaining {unspecifiedCarbs}g
    </Button>
  {/if}

  <div class="space-y-2">
    <Label for="note">
      {!showPortions ? "Description" : "Note (optional)"}
    </Label>
    <Input
      id="note"
      bind:value={note}
      placeholder={!showPortions ? "What did you eat?" : "Add a note..."}
    />
  </div>
</div>
