<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Card, CardContent, CardHeader } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Separator } from "$lib/components/ui/separator";
  import { Trash2, MoveUp, ArrowRight } from "lucide-svelte";
  import type { QuickPickRecord, FoodRecord } from "./types";
  import { getFoodState } from "./food-context";
  import { scaleMacro, formatMacro, getGiLabel } from "$lib/components/food";

  interface Props {
    quickPick: QuickPickRecord;
    index: number;
  }

  let { quickPick, index }: Props = $props();

  const foodState = getFoodState();

  let isDragOver = $state(false);

  /**
   * Calculate scaled carbs for a food item.
   * If food has 4 servings = 269g carbs, and user wants 1 serving,
   * the scaled carbs = (269 / 4) * 1 = 67.25g
   */
  function getScaledCarbs(food: {
    portion: number;
    carbs: number;
    portions: number;
  }): number {
    const basePortion = food.portion || 1;
    const selectedPortions = food.portions || 1;
    return scaleMacro(basePortion, selectedPortions, food.carbs);
  }

  function handleDrop(event: DragEvent) {
    event.preventDefault();
    isDragOver = false;

    try {
      const foodData = event.dataTransfer?.getData("application/json");
      if (!foodData) return;
      const food: FoodRecord = JSON.parse(foodData);
      foodState.addFoodToQuickPick(index, food);
    } catch (error) {
      console.error("Error handling drop:", error);
    }
  }

  function handleDragOver(event: DragEvent) {
    event.preventDefault();
    isDragOver = true;
    if (event.dataTransfer) {
      event.dataTransfer.dropEffect = "copy";
    }
  }

  function handleDragLeave(event: DragEvent) {
    // Only set isDragOver to false if we're actually leaving the drop zone
    const rect = (event.currentTarget as HTMLElement).getBoundingClientRect();
    const x = event.clientX;
    const y = event.clientY;

    if (x < rect.left || x > rect.right || y < rect.top || y > rect.bottom) {
      isDragOver = false;
    }
  }

  function updateName(event: Event) {
    const target = event.target as HTMLInputElement;
    foodState.updateQuickPickName(index, target.value);
  }

  function updatePortions(foodIndex: number, event: Event) {
    const target = event.target as HTMLInputElement;
    const portions = parseFloat(target.value) || 0;
    foodState.updateQuickPickPortions(index, foodIndex, portions);
  }
</script>

<Card
  class="border-2 border-dashed transition-all duration-200 {isDragOver
    ? 'border-primary bg-primary/5'
    : 'border-muted-foreground/25 hover:border-muted-foreground/50'}"
  ondrop={handleDrop}
  ondragover={handleDragOver}
  ondragleave={handleDragLeave}
>
  <CardHeader class="pb-2">
    <div class="flex items-center gap-2 flex-wrap">
      <Button
        variant="ghost"
        size="sm"
        onclick={() => foodState.moveQuickPickToTop(index)}
        title="Move to the top"
      >
        <MoveUp class="h-4 w-4" />
      </Button>
      <Separator orientation="vertical" class="h-4" />
      <Button
        variant="ghost"
        size="sm"
        onclick={() => foodState.deleteQuickPick(index)}
        title="Delete quick pick"
      >
        <Trash2 class="h-4 w-4" />
      </Button>
      <Separator orientation="vertical" class="h-4" />
      <div class="flex items-center gap-2">
        <Label for="name-{index}" class="whitespace-nowrap">Name:</Label>
        <Input
          id="name-{index}"
          value={quickPick.name}
          oninput={updateName}
          class="w-32"
          placeholder="Quick pick name"
        />
      </div>
      <div class="flex items-center gap-2">
        <Checkbox
          id="hidden-{index}"
          checked={quickPick.hidden}
          onCheckedChange={(checked) =>
            foodState.updateQuickPickHidden(index, checked)}
        />
        <Label for="hidden-{index}">Hidden</Label>
      </div>
      <div class="flex items-center gap-2">
        <Checkbox
          id="hideafteruse-{index}"
          checked={quickPick.hideafteruse}
          onCheckedChange={(checked) =>
            foodState.updateQuickPickHideAfterUse(index, checked)}
        />
        <Label for="hideafteruse-{index}">Hide after use</Label>
      </div>
      <Separator orientation="vertical" class="h-4" />
      <span
        class="text-sm font-medium bg-primary/10 text-primary px-2 py-1 rounded"
      >
        Total: {formatMacro(quickPick.carbs)}g carbs
      </span>
    </div>
  </CardHeader>
  <CardContent>
    {#if quickPick.foods.length > 0}
      <div class="space-y-2">
        <div
          class="grid grid-cols-[auto_1fr_auto_auto_auto] gap-3 text-sm font-medium text-muted-foreground border-b pb-1"
        >
          <div class="w-8"></div>
          <div>Food</div>
          <div class="text-center w-24">Servings</div>
          <div class="text-center w-16">GI</div>
          <div class="text-right w-24">Carbs</div>
        </div>
        {#each quickPick.foods as food, foodIndex}
          {@const scaledCarbs = getScaledCarbs(food)}
          <div
            class="grid grid-cols-[auto_1fr_auto_auto_auto] gap-3 text-sm items-center py-1 hover:bg-muted/25 rounded"
          >
            <div class="w-8">
              <Button
                variant="ghost"
                size="sm"
                onclick={() => foodState.deleteQuickPickFood(index, foodIndex)}
                title="Remove food"
              >
                <Trash2 class="h-3 w-3" />
              </Button>
            </div>
            <div class="truncate" title={food.name}>
              <span class="font-medium">{food.name}</span>
              <span class="text-muted-foreground text-xs ml-1">
                ({food.portion} {food.unit} = {formatMacro(food.carbs)}g)
              </span>
            </div>
            <div class="text-center w-24">
              <Input
                type="number"
                value={food.portions}
                oninput={(e) => updatePortions(foodIndex, e)}
                class="w-20 h-7 text-xs text-center"
                min="0"
                step="0.5"
              />
            </div>
            <div class="text-center w-16 text-muted-foreground">
              {getGiLabel(food.gi)}
            </div>
            <div class="text-right w-24 font-semibold text-primary">
              {formatMacro(scaledCarbs)}g
            </div>
          </div>
        {/each}
      </div>
    {:else}
      <div
        class="text-center text-muted-foreground italic py-8 border-2 border-dashed border-muted-foreground/25 rounded-lg"
      >
        {#if isDragOver}
          <div class="text-primary font-medium">Drop food here!</div>
        {:else}
          <div class="flex items-center justify-center gap-2">
            <ArrowRight class="w-4 h-4" />
            Drag & drop food here
          </div>
        {/if}
      </div>
    {/if}
  </CardContent>
</Card>
