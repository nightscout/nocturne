<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import {
    Card,
    CardContent,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { getFoodState } from "./food-context";
  import {
    CategorySubcategoryCombobox,
    UnitCombobox,
    GiCombobox,
  } from "$lib/components/food";

  const foodStore = getFoodState();

  function handleCategoryChange(category: string) {
    foodStore.currentFood.category = category;
  }

  function handleSubcategoryChange(subcategory: string) {
    foodStore.currentFood.subcategory = subcategory;
  }

  function handleCategoryCreate(category: string) {
    if (!foodStore.categories[category]) {
      foodStore.categories[category] = {};
    }
  }

  function handleSubcategoryCreate(category: string, subcategory: string) {
    if (!foodStore.categories[category]) {
      foodStore.categories[category] = {};
    }
    foodStore.categories[category][subcategory] = true;
  }

  function handleUnitChange(unit: string) {
    foodStore.currentFood.unit = unit;
  }

  function handleGiChange(gi: number) {
    foodStore.currentFood.gi = gi;
  }

  function handleSaveFood() {
    foodStore.saveFood();
  }

  function handleClearForm() {
    foodStore.clearForm();
  }
</script>

<Card>
  <CardHeader class="pb-4">
    <CardTitle class="flex items-center gap-2">
      {foodStore.currentFood._id ? "Edit Food" : "New Food"}
      {#if foodStore.currentFood._id}
        <span class="text-sm font-normal text-muted-foreground">
          ID: {foodStore.currentFood._id}
        </span>
      {/if}
    </CardTitle>
  </CardHeader>
  <CardContent class="space-y-6">
    <!-- Primary info: Name -->
    <div class="space-y-2">
      <Label for="food-name">Name</Label>
      <Input
        id="food-name"
        bind:value={foodStore.currentFood.name}
        placeholder="Enter food name"
        class="max-w-md"
      />
    </div>

    <!-- Most important: Carbs, GI, Portion, Unit -->
    <div class="rounded-lg border bg-muted/30 p-4 space-y-4">
      <h3 class="text-sm font-medium text-muted-foreground">
        Key Nutritional Info
      </h3>
      <div class="grid grid-cols-2 md:grid-cols-4 gap-4">
        <div class="space-y-2">
          <Label for="food-carbs" class="text-base font-semibold"
            >Carbs (g)</Label
          >
          <Input
            id="food-carbs"
            type="number"
            bind:value={foodStore.currentFood.carbs}
            class="text-lg font-medium"
          />
        </div>
        <div class="space-y-2">
          <Label for="food-gi" class="text-base font-semibold"
            >Glycemic Index</Label
          >
          <GiCombobox
            value={foodStore.currentFood.gi}
            onValueChange={handleGiChange}
          />
        </div>
        <div class="space-y-2">
          <Label for="food-portion">Portion</Label>
          <Input
            id="food-portion"
            type="number"
            bind:value={foodStore.currentFood.portion}
          />
        </div>
        <div class="space-y-2">
          <Label for="food-unit">Unit</Label>
          <UnitCombobox
            value={foodStore.currentFood.unit}
            onValueChange={handleUnitChange}
          />
        </div>
      </div>
    </div>

    <!-- Secondary info: Category, Other Macros -->
    <div class="grid grid-cols-1 md:grid-cols-2 gap-6">
      <!-- Category section -->
      <div class="space-y-4">
        <h3 class="text-sm font-medium text-muted-foreground">Category</h3>
        <CategorySubcategoryCombobox
          bind:category={foodStore.currentFood.category}
          bind:subcategory={foodStore.currentFood.subcategory}
          categories={foodStore.categories}
          onCategoryChange={handleCategoryChange}
          onSubcategoryChange={handleSubcategoryChange}
          onCategoryCreate={handleCategoryCreate}
          onSubcategoryCreate={handleSubcategoryCreate}
        />
      </div>

      <!-- Other macros section -->
      <div class="space-y-4">
        <h3 class="text-sm font-medium text-muted-foreground">
          Additional Macros
        </h3>
        <div class="grid grid-cols-3 gap-3">
          <div class="space-y-2">
            <Label for="food-fat">Fat (g)</Label>
            <Input
              id="food-fat"
              type="number"
              bind:value={foodStore.currentFood.fat}
            />
          </div>
          <div class="space-y-2">
            <Label for="food-protein">Protein (g)</Label>
            <Input
              id="food-protein"
              type="number"
              bind:value={foodStore.currentFood.protein}
            />
          </div>
          <div class="space-y-2">
            <Label for="food-energy">Energy (kJ)</Label>
            <Input
              id="food-energy"
              type="number"
              bind:value={foodStore.currentFood.energy}
            />
          </div>
        </div>
      </div>
    </div>

    <!-- Actions -->
    <div class="flex gap-2 pt-2">
      <Button onclick={handleSaveFood}>
        {foodStore.currentFood._id ? "Save Changes" : "Create Food"}
      </Button>
      <Button variant="outline" onclick={handleClearForm}>Clear</Button>
    </div>
  </CardContent>
</Card>
