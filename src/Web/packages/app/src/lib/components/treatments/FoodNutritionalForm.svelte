<script lang="ts">
  import * as Popover from "$lib/components/ui/popover";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import * as Command from "$lib/components/ui/command";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Check,
    ChevronsUpDown,
    Star,
    ChevronDown,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { tick } from "svelte";
  import { CategorySubcategoryCombobox } from "$lib/components/food";
  import type { Food } from "$lib/api";

  interface Props {
    foodName: string;
    foodCategory: string;
    foodSubcategory: string;
    foodPortion: number | null;
    foodUnit: string;
    foodCarbs: number | null;
    foodFat: number | null;
    foodProtein: number | null;
    foodEnergy: number | null;
    foodGi: number;
    selectedFood: Food | null;
    favorites: Food[];
    nutritionDetailsOpen: boolean;
    categories: Record<string, Record<string, boolean>>;
    isCreatingNew: boolean;
    onToggleFavorite: () => void;
    onsubmit?: (event: SubmitEvent) => void;
  }

  let {
    foodName = $bindable(),
    foodCategory = $bindable(),
    foodSubcategory = $bindable(),
    foodPortion = $bindable(),
    foodUnit = $bindable(),
    foodCarbs = $bindable(),
    foodFat = $bindable(),
    foodProtein = $bindable(),
    foodEnergy = $bindable(),
    foodGi = $bindable(),
    selectedFood,
    favorites,
    nutritionDetailsOpen = $bindable(),
    categories,
    isCreatingNew,
    onToggleFavorite,
    onsubmit,
  }: Props = $props();

  // Unit combobox state
  let unitOpen = $state(false);
  let unitTriggerRef = $state<HTMLButtonElement>(null!);

  // GI combobox state
  let giOpen = $state(false);
  let giTriggerRef = $state<HTMLButtonElement>(null!);

  // Constants
  const foodUnits = ["g", "ml", "pcs", "oz"];
  const giOptions = [
    { value: 1, label: "Low" },
    { value: 2, label: "Medium" },
    { value: 3, label: "High" },
  ];

  // Derived: display labels
  const selectedUnitLabel = $derived(foodUnit || "Select unit...");
  const selectedGiLabel = $derived(
    giOptions.find((opt) => opt.value === foodGi)?.label || "Select GI..."
  );

  function closeUnitAndFocus() {
    unitOpen = false;
    tick().then(() => unitTriggerRef?.focus());
  }

  function closeGiAndFocus() {
    giOpen = false;
    tick().then(() => giTriggerRef?.focus());
  }

  function selectUnit(unit: string) {
    foodUnit = unit;
    closeUnitAndFocus();
  }

  function selectGi(gi: number) {
    foodGi = gi;
    closeGiAndFocus();
  }
</script>

<form
  id="food-form"
  class="@container border-t pt-4 space-y-4"
  {onsubmit}
>
  <!-- Hidden fields for food record -->
  <input type="hidden" name="type" value="food" />
  {#if selectedFood?._id}
    <input type="hidden" name="_id" value={selectedFood._id} />
  {/if}
  <input type="hidden" name="hideafteruse" value="false" />
  <input type="hidden" name="hidden" value="false" />
  <input type="hidden" name="position" value="0" />

  <!-- Name and Category row -->
  <div class="grid gap-4 @lg:grid-cols-3">
    <div class="space-y-2">
      <Label for="food-name">Name</Label>
      <Input id="food-name" name="name" bind:value={foodName} />
    </div>
    <div class="space-y-2 col-span-2">
      <Label>Category & Subcategory</Label>
      <CategorySubcategoryCombobox
        bind:category={foodCategory}
        bind:subcategory={foodSubcategory}
        {categories}
        onCategoryChange={(cat) => (foodCategory = cat)}
        onSubcategoryChange={(sub) => (foodSubcategory = sub)}
        onCategoryCreate={(_cat) => {
          // Category will be created when food is saved
        }}
        onSubcategoryCreate={(_cat, _sub) => {
          // Subcategory will be created when food is saved
        }}
      />
    </div>
  </div>
  <!-- Hidden inputs for category/subcategory since combobox doesn't render native inputs -->
  <input type="hidden" name="category" value={foodCategory} />
  <input type="hidden" name="subcategory" value={foodSubcategory} />

  <!-- Quick summary of selected food -->
  {#if selectedFood && !isCreatingNew}
    <div
      class="flex items-center justify-between rounded-lg border bg-muted/30 p-3"
    >
      <div class="flex items-center gap-4">
        <div class="text-sm">
          <span class="font-medium">{foodPortion}{foodUnit}</span>
          <span class="text-muted-foreground">=</span>
          <span class="font-semibold text-green-600 dark:text-green-400">
            {foodCarbs}g carbs
          </span>
        </div>
        {#if (foodFat ?? 0) > 0 || (foodProtein ?? 0) > 0}
          <div class="text-xs text-muted-foreground">
            {foodFat}g fat • {foodProtein}g protein
          </div>
        {/if}
      </div>
      <Button
        type="button"
        variant="ghost"
        size="sm"
        onclick={onToggleFavorite}
      >
        <Star
          class="h-4 w-4 {favorites.some((fav) => fav._id === selectedFood?._id)
            ? 'text-yellow-500 fill-yellow-500'
            : 'text-muted-foreground'}"
        />
      </Button>
    </div>
  {/if}

  <!-- Collapsible nutritional details -->
  <Collapsible.Root bind:open={nutritionDetailsOpen}>
    <Collapsible.Trigger
      class="flex w-full items-center justify-between rounded-lg border px-3 py-2 text-sm font-medium hover:bg-muted/50 transition-colors"
    >
      <span>Nutritional Details</span>
      <ChevronDown
        class={cn(
          "h-4 w-4 transition-transform",
          nutritionDetailsOpen && "rotate-180"
        )}
      />
    </Collapsible.Trigger>
    <Collapsible.Content class="pt-3 space-y-4">
      <!-- Portion and unit row -->
      <div class="grid gap-4 @lg:grid-cols-4">
        <div class="space-y-2">
          <Label for="food-portion">Portion Size</Label>
          <Input
            id="food-portion"
            name="portion"
            type="number"
            bind:value={foodPortion}
          />
        </div>
        <div class="space-y-2">
          <Label for="food-unit">Unit</Label>
          <input type="hidden" name="unit" value={foodUnit} />
          <Popover.Root bind:open={unitOpen}>
            <Popover.Trigger bind:ref={unitTriggerRef}>
              {#snippet child({ props })}
                <Button
                  variant="outline"
                  class="w-full justify-between"
                  {...props}
                  role="combobox"
                  aria-expanded={unitOpen}
                >
                  {selectedUnitLabel}
                  <ChevronsUpDown class="ml-2 size-4 shrink-0 opacity-50" />
                </Button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-(--bits-popover-anchor-width) p-0">
              <Command.Root>
                <Command.Input placeholder="Search units..." />
                <Command.List>
                  <Command.Empty>No unit found.</Command.Empty>
                  <Command.Group>
                    {#each foodUnits as unit}
                      <Command.Item
                        value={unit}
                        onSelect={() => selectUnit(unit)}
                      >
                        <Check
                          class={cn(
                            "mr-2 size-4",
                            foodUnit !== unit && "text-transparent"
                          )}
                        />
                        {unit}
                      </Command.Item>
                    {/each}
                  </Command.Group>
                </Command.List>
              </Command.Root>
            </Popover.Content>
          </Popover.Root>
        </div>
        <div class="space-y-2">
          <Label for="food-carbs">Carbs (g)</Label>
          <Input
            id="food-carbs"
            name="carbs"
            type="number"
            bind:value={foodCarbs}
          />
        </div>
        <div class="space-y-2">
          <Label for="food-gi">GI</Label>
          <input type="hidden" name="gi" value={foodGi} />
          <Popover.Root bind:open={giOpen}>
            <Popover.Trigger bind:ref={giTriggerRef}>
              {#snippet child({ props })}
                <Button
                  variant="outline"
                  class="w-full justify-between"
                  {...props}
                  role="combobox"
                  aria-expanded={giOpen}
                >
                  {selectedGiLabel}
                  <ChevronsUpDown class="ml-2 size-4 shrink-0 opacity-50" />
                </Button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-(--bits-popover-anchor-width) p-0">
              <Command.Root>
                <Command.Input placeholder="Search GI..." />
                <Command.List>
                  <Command.Empty>No GI found.</Command.Empty>
                  <Command.Group>
                    {#each giOptions as option}
                      <Command.Item
                        value={option.label}
                        onSelect={() => selectGi(option.value)}
                      >
                        <Check
                          class={cn(
                            "mr-2 size-4",
                            foodGi !== option.value && "text-transparent"
                          )}
                        />
                        {option.label}
                      </Command.Item>
                    {/each}
                  </Command.Group>
                </Command.List>
              </Command.Root>
            </Popover.Content>
          </Popover.Root>
        </div>
      </div>

      <!-- Additional nutrients row -->
      <div class="grid gap-4 @lg:grid-cols-3">
        <div class="space-y-2">
          <Label for="food-fat">Fat (g)</Label>
          <Input id="food-fat" name="fat" type="number" bind:value={foodFat} />
        </div>
        <div class="space-y-2">
          <Label for="food-protein">Protein (g)</Label>
          <Input
            id="food-protein"
            name="protein"
            type="number"
            bind:value={foodProtein}
          />
        </div>
        <div class="space-y-2">
          <Label for="food-energy">Energy (kJ)</Label>
          <Input
            id="food-energy"
            name="energy"
            type="number"
            bind:value={foodEnergy}
          />
        </div>
      </div>
    </Collapsible.Content>
  </Collapsible.Root>
</form>
