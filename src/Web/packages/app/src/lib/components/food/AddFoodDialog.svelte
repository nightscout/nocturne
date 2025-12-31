<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { toast } from "svelte-sonner";
  import type { Food } from "$lib/api";
  import { CategorySubcategoryCombobox } from "$lib/components/food";
  import UnitCombobox from "./UnitCombobox.svelte";
  import GiCombobox from "./GiCombobox.svelte";
  import { DEFAULT_PORTION, DEFAULT_UNIT, DEFAULT_GI } from "./food-constants";
  import {
    createNewFood,
    updateExistingFood,
  } from "$lib/data/treatment-foods.remote";

  interface Props {
    /** Whether the dialog is open */
    open: boolean;
    /** Callback when open state changes */
    onOpenChange: (open: boolean) => void;
    /** Initial food data to edit (optional) */
    initialFood?: Food | null;
    /** Categories map from parent */
    categories: Record<string, Record<string, boolean>>;
    /** Callback when food is saved (created or updated) */
    onSave?: (food: Food) => void;
    /** Callback when a new category is created */
    onCategoryCreate?: (category: string) => void;
    /** Callback when a new subcategory is created */
    onSubcategoryCreate?: (category: string, subcategory: string) => void;
  }

  let {
    open = $bindable(),
    onOpenChange,
    initialFood = null,
    categories,
    onSave,
    onCategoryCreate,
    onSubcategoryCreate,
  }: Props = $props();

  // Editable food fields
  let foodName = $state("");
  let foodCategory = $state("");
  let foodSubcategory = $state("");
  let foodPortion = $state<number>(DEFAULT_PORTION);
  let foodUnit = $state(DEFAULT_UNIT);
  let foodCarbs = $state<number>(0);
  let foodFat = $state<number>(0);
  let foodProtein = $state<number>(0);
  let foodEnergy = $state<number>(0);
  let foodGi = $state<number>(DEFAULT_GI);

  // Track if editing existing or creating new
  let editingFoodId = $state<string | undefined>(undefined);
  let isSaving = $state(false);

  // Initialize form from initialFood when dialog opens
  $effect(() => {
    if (open) {
      if (initialFood) {
        populateFromFood(initialFood);
      } else {
        resetForm();
      }
    }
  });

  function populateFromFood(food: Food) {
    editingFoodId = food._id;
    foodName = food.name ?? "";
    foodCategory = food.category ?? "";
    foodSubcategory = food.subcategory ?? "";
    foodPortion = food.portion ?? DEFAULT_PORTION;
    foodUnit = food.unit ?? DEFAULT_UNIT;
    foodCarbs = food.carbs ?? 0;
    foodFat = food.fat ?? 0;
    foodProtein = food.protein ?? 0;
    foodEnergy = food.energy ?? 0;
    foodGi = food.gi ?? DEFAULT_GI;
  }

  function resetForm() {
    editingFoodId = undefined;
    foodName = "";
    foodCategory = "";
    foodSubcategory = "";
    foodPortion = DEFAULT_PORTION;
    foodUnit = DEFAULT_UNIT;
    foodCarbs = 0;
    foodFat = 0;
    foodProtein = 0;
    foodEnergy = 0;
    foodGi = DEFAULT_GI;
    isSaving = false;
  }

  function buildFoodRecord(): Omit<Food, "_id"> & { _id?: string } {
    return {
      _id: editingFoodId,
      type: "food",
      name: foodName,
      category: foodCategory,
      subcategory: foodSubcategory,
      portion: foodPortion,
      unit: foodUnit,
      carbs: foodCarbs,
      fat: foodFat,
      protein: foodProtein,
      energy: foodEnergy,
      gi: foodGi,
    };
  }

  async function handleSave() {
    if (!foodName.trim()) {
      toast.error("Please enter a food name");
      return;
    }

    isSaving = true;
    try {
      const foodRecord = buildFoodRecord();

      if (editingFoodId) {
        // Update existing
        await updateExistingFood(foodRecord as any);
        toast.success("Food updated successfully");
        onSave?.({ ...foodRecord, _id: editingFoodId } as Food);
      } else {
        // Create new
        delete foodRecord._id;
        const result = await createNewFood(foodRecord as any);
        if (result.success && result.record) {
          toast.success("Food created successfully");
          onSave?.(result.record as Food);
        } else {
          throw new Error("Failed to create food");
        }
      }

      onOpenChange(false);
    } catch (err) {
      console.error("Failed to save food:", err);
      toast.error("Failed to save food");
    } finally {
      isSaving = false;
    }
  }

  function handleCancel() {
    onOpenChange(false);
  }

  const canSave = $derived(foodName.trim() !== "" && !isSaving);
  const dialogTitle = $derived(editingFoodId ? "Edit Food" : "Add Food");
  const saveButtonLabel = $derived(
    isSaving ? "Saving..." : editingFoodId ? "Update" : "Create"
  );
</script>

<Dialog.Root bind:open {onOpenChange}>
  <Dialog.Content class="max-w-2xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>{dialogTitle}</Dialog.Title>
      <Dialog.Description>
        {editingFoodId
          ? "Update the food's nutritional information."
          : "Create a new food with nutritional information."}
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4">
      <!-- Name and Category row -->
      <div class="grid gap-4 md:grid-cols-3">
        <div class="space-y-2">
          <Label for="food-name">Name</Label>
          <Input id="food-name" bind:value={foodName} />
        </div>
        <div class="space-y-2 col-span-2">
          <Label>Category & Subcategory</Label>
          <CategorySubcategoryCombobox
            bind:category={foodCategory}
            bind:subcategory={foodSubcategory}
            {categories}
            onCategoryChange={(cat) => (foodCategory = cat)}
            onSubcategoryChange={(sub) => (foodSubcategory = sub)}
            {onCategoryCreate}
            {onSubcategoryCreate}
          />
        </div>
      </div>

      <!-- Carbs, GI, Portion, Unit row - Key nutritional info emphasized -->
      <div class="rounded-lg border bg-muted/30 p-4 space-y-4">
        <h3 class="text-sm font-medium text-muted-foreground">
          Key Nutritional Info
        </h3>
        <div class="grid gap-4 md:grid-cols-4">
          <div class="space-y-2">
            <Label for="food-carbs" class="text-base font-semibold">
              Carbs (g)
            </Label>
            <Input
              id="food-carbs"
              type="number"
              bind:value={foodCarbs}
              class="text-lg font-medium"
            />
          </div>
          <div class="space-y-2">
            <Label for="food-gi" class="text-base font-semibold">
              Glycemic Index
            </Label>
            <GiCombobox value={foodGi} onValueChange={(gi) => (foodGi = gi)} />
          </div>
          <div class="space-y-2">
            <Label for="food-portion">Portion</Label>
            <Input id="food-portion" type="number" bind:value={foodPortion} />
          </div>
          <div class="space-y-2">
            <Label for="food-unit">Unit</Label>
            <UnitCombobox
              value={foodUnit}
              onValueChange={(unit) => (foodUnit = unit)}
            />
          </div>
        </div>
      </div>

      <!-- Additional Macros row -->
      <div class="space-y-2">
        <h3 class="text-sm font-medium text-muted-foreground">
          Additional Macros
        </h3>
        <div class="grid gap-4 md:grid-cols-3">
          <div class="space-y-2">
            <Label for="food-fat">Fat (g)</Label>
            <Input id="food-fat" type="number" bind:value={foodFat} />
          </div>
          <div class="space-y-2">
            <Label for="food-protein">Protein (g)</Label>
            <Input id="food-protein" type="number" bind:value={foodProtein} />
          </div>
          <div class="space-y-2">
            <Label for="food-energy">Energy (kJ)</Label>
            <Input id="food-energy" type="number" bind:value={foodEnergy} />
          </div>
        </div>
      </div>
    </div>

    <Dialog.Footer class="gap-2">
      <Button type="button" variant="outline" onclick={handleCancel}>
        Cancel
      </Button>
      <Button type="button" onclick={handleSave} disabled={!canSave}>
        {saveButtonLabel}
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
