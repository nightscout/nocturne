<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Loader2 } from "lucide-svelte";
  import { toast } from "svelte-sonner";
  import { useToastSubmission } from "$lib/forms";
  import {
    CarbIntakeFoodInputMode,
    type Food,
    type CarbIntakeFoodRequest,
  } from "$lib/api";
  import {
    getFoods as getAllFoods,
    createFood as createNewFood,
    updateFood as updateExistingFood,
  } from "$api/generated/foods.generated.remote";
  import {
    getFavorites as getFavoriteFoods,
    addFavorite as addFavoriteFood,
    removeFavorite as removeFavoriteFood,
    getRecentFoods,
  } from "$api/generated/foods.generated.remote";
  import FoodSearchCombobox from "./FoodSearchCombobox.svelte";
  import FoodNutritionalForm from "./FoodNutritionalForm.svelte";
  import TreatmentPortionsForm from "./TreatmentPortionsForm.svelte";

  interface Props {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    onSubmit: (request: CarbIntakeFoodRequest, displayName?: string) => void;
    /** Total carbs from the treatment */
    totalCarbs?: number;
    /** Remaining unspecified carbs */
    unspecifiedCarbs?: number;
    /** Whether the owner's `onSubmit` is still in flight. */
    submitting?: boolean;
  }

  let {
    open = $bindable(),
    onOpenChange,
    onSubmit,
    totalCarbs = 0,
    unspecifiedCarbs = 0,
    submitting = false,
  }: Props = $props();

  // Food lists
  let favorites = $state<Food[]>([]);
  let recents = $state<Food[]>([]);
  let allFoods = $state<Food[]>([]);

  // Combobox state
  let searchQuery = $state("");

  // Selected/editing food state
  let selectedFood = $state<Food | null>(null);
  let originalFood = $state<Food | null>(null);
  let isCreatingNew = $state(false);
  let isLoggingWithoutSaving = $state(false);

  // Editable food fields
  let foodName = $state("");
  let foodCategory = $state("");
  let foodSubcategory = $state("");
  let foodPortion = $state<number>(100);
  let foodUnit = $state("g");
  let foodCarbs = $state<number>(0);
  let foodFat = $state<number>(0);
  let foodProtein = $state<number>(0);
  let foodEnergy = $state<number>(0);
  let foodGi = $state<number>(2);

  // Treatment request fields - both portions and carbs are editable
  let portions = $state(1);
  let entryCarbs = $state(0);
  let timeOffsetMinutes = $state<number>(0);
  let note = $state("");

  // Track which field was last edited
  let lastEditedField = $state<"portions" | "carbs">("portions");

  // Collapsible state for food details
  let nutritionDetailsOpen = $state(false);

  // Track which submit action to take
  let submitAction = $state<"create" | "update" | "saveAsNew">("create");

  const foodSave = useToastSubmission("Failed to save food");

  async function handleFoodFormSubmit(event: SubmitEvent) {
    event.preventDefault();
    await foodSave.run(async () => {
      const foodPayload: Food = {
        _id: submitAction === "update" ? selectedFood?._id : undefined,
        type: "food",
        name: foodName.trim(),
        category: foodCategory,
        subcategory: foodSubcategory,
        portion: foodPortion,
        unit: foodUnit,
        carbs: foodCarbs,
        fat: foodFat,
        protein: foodProtein,
        energy: foodEnergy,
        gi: foodGi,
      } as Food;

      if (submitAction === "create" || submitAction === "saveAsNew") {
        const newFood = (await createNewFood(foodPayload)) as Food;
        allFoods = [...allFoods, newFood];
        selectedFood = newFood;
        originalFood = { ...newFood };
        toast.success("Food created successfully");

        const request = buildFoodRequest();
        request.foodId = newFood._id!;
        onSubmit(request, newFood.name ?? foodName.trim());
      } else if (submitAction === "update" && selectedFood?._id) {
        const updated = (await updateExistingFood({
          foodId: selectedFood._id,
          request: foodPayload,
        })) as Food;

        const idx = allFoods.findIndex((f) => f._id === selectedFood?._id);
        if (idx !== -1) {
          allFoods[idx] = { ...allFoods[idx], ...updated };
        }
        originalFood = { ...selectedFood!, ...updated };
        toast.success("Food updated successfully");

        const request = buildFoodRequest();
        request.foodId = selectedFood._id;
        onSubmit(request, selectedFood?.name ?? "Food");
      }
    });
  }

  // Handle portions change - recalculate carbs
  function handlePortionsChange(value: number) {
    portions = value;
    lastEditedField = "portions";
    if (foodCarbs > 0) {
      entryCarbs = Math.round(value * foodCarbs * 10) / 10;
    }
  }

  // Handle carbs change - recalculate portions
  function handleCarbsChange(value: number) {
    entryCarbs = value;
    lastEditedField = "carbs";
    if (foodCarbs > 0) {
      portions = Math.round((value / foodCarbs) * 100) / 100;
    }
  }

  // Scale to fill remaining unspecified carbs
  function scaleToFit() {
    if (unspecifiedCarbs > 0) {
      // For log without saving, directly set the carbs
      // For food selection, handleCarbsChange recalculates portions
      if (isLoggingWithoutSaving) {
        entryCarbs = Math.round(unspecifiedCarbs * 10) / 10;
      } else if (foodCarbs > 0) {
        handleCarbsChange(Math.round(unspecifiedCarbs * 10) / 10);
      }
    }
  }

  // Loading states
  let isLoading = $state(false);
  let isSubmitting = $state(false);


  // Derived: check if any field has been edited
  const hasEdits = $derived.by(() => {
    if (!originalFood) return false;
    return (
      foodName !== (originalFood.name ?? "") ||
      foodCategory !== (originalFood.category ?? "") ||
      foodSubcategory !== (originalFood.subcategory ?? "") ||
      foodPortion !== (originalFood.portion ?? 100) ||
      foodUnit !== (originalFood.unit ?? "g") ||
      foodCarbs !== (originalFood.carbs ?? 0) ||
      foodFat !== (originalFood.fat ?? 0) ||
      foodProtein !== (originalFood.protein ?? 0) ||
      foodEnergy !== (originalFood.energy ?? 0) ||
      foodGi !== (originalFood.gi ?? 2)
    );
  });


  // Derived: categories from all foods
  const categories = $derived.by(() => {
    const catMap: Record<string, Record<string, boolean>> = {};
    for (const food of allFoods) {
      if (food.category) {
        if (!catMap[food.category]) {
          catMap[food.category] = {};
        }
        if (food.subcategory) {
          catMap[food.category][food.subcategory] = true;
        }
      }
    }
    return catMap;
  });


  $effect(() => {
    if (!open) {
      resetForm();
      return;
    }
    void loadFoods();
  });

  async function loadFoods() {
    isLoading = true;
    try {
      // Use Promise.allSettled to handle auth-required endpoints gracefully
      // favorites and recents require authentication, but allFoods doesn't
      const [favoriteResult, recentResult, allResult] =
        await Promise.allSettled([
          getFavoriteFoods(),
          getRecentFoods({ limit: 5 }),
          getAllFoods(undefined),
        ]);

      // Extract successful results, defaulting to empty arrays on failure
      favorites =
        favoriteResult.status === "fulfilled" ? favoriteResult.value : [];
      recents = recentResult.status === "fulfilled" ? recentResult.value : [];
      allFoods = allResult.status === "fulfilled" ? allResult.value : [];

      // Log any failures for debugging (but don't fail the whole operation)
      if (favoriteResult.status === "rejected") {
        console.debug(
          "Could not load favorites (user may not be authenticated)"
        );
      }
      if (recentResult.status === "rejected") {
        console.debug(
          "Could not load recent foods (user may not be authenticated)"
        );
      }
      if (allResult.status === "rejected") {
        console.error("Failed to load food list:", allResult.reason);
      }
    } catch (err) {
      console.error("Failed to load foods:", err);
    } finally {
      isLoading = false;
    }
  }

  function resetForm() {
    selectedFood = null;
    originalFood = null;
    isCreatingNew = false;
    isLoggingWithoutSaving = false;
    searchQuery = "";

    // Reset food fields
    foodName = "";
    foodCategory = "";
    foodSubcategory = "";
    foodPortion = 100;
    foodUnit = "g";
    foodCarbs = 0;
    foodFat = 0;
    foodProtein = 0;
    foodEnergy = 0;
    foodGi = 2;

    // Reset treatment fields
    portions = 1;
    entryCarbs = 0;
    timeOffsetMinutes = 0;
    note = "";
    lastEditedField = "portions";
    nutritionDetailsOpen = false;
    isSubmitting = false;
    submitAction = "create";
  }

  function selectFood(food: Food) {
    selectedFood = food;
    originalFood = { ...food };

    // Populate editable fields
    foodName = food.name ?? "";
    foodCategory = food.category ?? "";
    foodSubcategory = food.subcategory ?? "";
    foodPortion = food.portion ?? 100;
    foodUnit = food.unit ?? "g";
    foodCarbs = food.carbs ?? 0;
    foodFat = food.fat ?? 0;
    foodProtein = food.protein ?? 0;
    foodEnergy = food.energy ?? 0;
    foodGi = food.gi ?? 2;

    // Reset treatment fields - initialize carbs based on 1 portion
    portions = 1;
    entryCarbs = food.carbs ?? 0;
    timeOffsetMinutes = 0;
    note = "";
    lastEditedField = "portions";
    isCreatingNew = false;
  }

  function startCreateNew() {
    selectedFood = null;
    originalFood = null;
    isCreatingNew = true;
    isLoggingWithoutSaving = false;

    // Use search query as initial name
    foodName = searchQuery.trim();
    foodCategory = "";
    foodSubcategory = "";
    foodPortion = 100;
    foodUnit = "g";
    foodCarbs = 0;
    foodFat = 0;
    foodProtein = 0;
    foodEnergy = 0;
    foodGi = 2;

    // Reset treatment fields
    portions = 1;
    entryCarbs = 0;
    timeOffsetMinutes = 0;
    note = "";
    lastEditedField = "portions";
  }

  function startLogWithoutSaving() {
    selectedFood = null;
    originalFood = null;
    isCreatingNew = false;
    isLoggingWithoutSaving = true;

    // Use search query as the note (describes what was eaten)
    note = searchQuery.trim();
    foodName = "";

    // Reset treatment fields - user will enter carbs directly
    portions = 1;
    entryCarbs = 0;
    timeOffsetMinutes = 0;
    lastEditedField = "carbs";
  }


  async function toggleFavorite(food: Food) {
    if (!food._id) return;
    try {
      const isFavorite = favorites.some((fav) => fav._id === food._id);
      if (isFavorite) {
        await removeFavoriteFood(food._id);
        favorites = favorites.filter((fav) => fav._id !== food._id);
      } else {
        await addFavoriteFood(food._id);
        favorites = [...favorites, food].sort((a, b) =>
          (a.name ?? "").localeCompare(b.name ?? "")
        );
        recents = recents.filter((recent) => recent._id !== food._id);
      }
    } catch (err) {
      console.error("Failed to update favorite:", err);
    }
  }

  function buildFoodRequest(): CarbIntakeFoodRequest {
    const request: CarbIntakeFoodRequest = {
      timeOffsetMinutes,
      note: note.trim() || undefined,
      inputMode:
        lastEditedField === "portions"
          ? CarbIntakeFoodInputMode.Portions
          : CarbIntakeFoodInputMode.Carbs,
    };

    if (lastEditedField === "portions") {
      request.portions = portions;
    } else {
      request.carbs = entryCarbs;
    }

    return request;
  }

  function handleAddFood() {
    if (!selectedFood?._id) return;

    const request = buildFoodRequest();
    request.foodId = selectedFood._id;

    isSubmitting = true;
    onSubmit(request, selectedFood?.name ?? "Food");
    isSubmitting = false;
  }

  // Handle logging without saving - submit with no foodId, just carbs and note
  function handleLogWithoutSaving() {
    if (entryCarbs <= 0) {
      toast.error("Please enter carbs amount");
      return;
    }

    const request: CarbIntakeFoodRequest = {
      foodId: undefined,
      carbs: entryCarbs,
      timeOffsetMinutes,
      note: note.trim() || undefined,
      inputMode: CarbIntakeFoodInputMode.Carbs,
    };

    isSubmitting = true;
    onSubmit(request, note.trim() || "Other");
    isSubmitting = false;
  }

  // Derived: show form when food selected, creating new, or logging without saving
  const showForm = $derived(
    selectedFood !== null || isCreatingNew || isLoggingWithoutSaving
  );

  // Derived: show simple carbs form for logging without saving
  const showSimpleCarbsForm = $derived(isLoggingWithoutSaving);

  // Derived: can submit
  const canSubmit = $derived(
    (selectedFood !== null ||
      (isCreatingNew && foodName.trim()) ||
      (isLoggingWithoutSaving && entryCarbs > 0)) &&
      !isSubmitting &&
      !submitting &&
      !foodSave.busy
  );
</script>

<Dialog.Root bind:open {onOpenChange}>
  <Dialog.Content class="max-w-4xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>Add Food</Dialog.Title>
      <Dialog.Description>
        Search for an existing food or create a new one.
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4">
      <!-- Food search combobox -->
      <FoodSearchCombobox
        {allFoods}
        {favorites}
        {recents}
        {selectedFood}
        bind:searchQuery
        {isLoading}
        onSelect={selectFood}
        onCreateNew={startCreateNew}
        onLogWithoutSaving={startLogWithoutSaving}
      />

      <!-- Log without saving - just show a message, then share the "How much did you eat?" section below -->
      {#if showSimpleCarbsForm}
        <div class="border-t pt-4">
          <div
            class="rounded-lg border bg-muted/30 p-3 text-sm text-muted-foreground"
          >
            Log carbs without creating a food entry. The note will be saved with
            this entry.
          </div>
        </div>
      {:else if showForm}
        <!-- Nutritional fields form -->
        <FoodNutritionalForm
          bind:foodName
          bind:foodCategory
          bind:foodSubcategory
          bind:foodPortion
          bind:foodUnit
          bind:foodCarbs
          bind:foodFat
          bind:foodProtein
          bind:foodEnergy
          bind:foodGi
          {selectedFood}
          {favorites}
          bind:nutritionDetailsOpen
          {categories}
          {isCreatingNew}
          onToggleFavorite={() => selectedFood && toggleFavorite(selectedFood)}
          onsubmit={handleFoodFormSubmit}
        />
      {/if}

      <!-- Treatment request fields - shared between food selection and log without saving -->
      {#if showForm || showSimpleCarbsForm}
        <TreatmentPortionsForm
          bind:portions
          bind:entryCarbs
          bind:timeOffsetMinutes
          bind:note
          {foodCarbs}
          {totalCarbs}
          {unspecifiedCarbs}
          showPortions={!showSimpleCarbsForm}
          onScaleToFit={scaleToFit}
          onPortionsChange={handlePortionsChange}
          onCarbsChange={handleCarbsChange}
        />
      {/if}
    </div>

    <Dialog.Footer class="gap-2 flex-wrap">
      <Button
        type="button"
        variant="outline"
        onclick={() => onOpenChange(false)}
      >
        Cancel
      </Button>

      {#if isLoggingWithoutSaving}
        <!-- Logging without saving food -->
        <Button
          type="button"
          onclick={handleLogWithoutSaving}
          disabled={!canSubmit || isSubmitting || submitting}
        >
          {isSubmitting || submitting ? "Logging..." : "Log Carbs"}
        </Button>
      {:else if isCreatingNew}
        <!-- Creating new food - submit the create form -->
        <Button
          type="submit"
          form="food-form"
          disabled={!canSubmit || foodSave.busy}
          onclick={() => { submitAction = "create"; }}
        >
          {#if foodSave.busy}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Saving...
          {:else}
            Save & Add
          {/if}
        </Button>
      {:else if selectedFood && hasEdits}
        <!-- Existing food with edits -->
        <Button
          type="submit"
          form="food-form"
          variant="outline"
          disabled={!canSubmit || foodSave.busy}
          onclick={() => { submitAction = "saveAsNew"; }}
        >
          {#if foodSave.busy}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Saving...
          {:else}
            Save as New
          {/if}
        </Button>
        <Button
          type="submit"
          form="food-form"
          disabled={!canSubmit || foodSave.busy}
          onclick={() => { submitAction = "update"; }}
        >
          {#if foodSave.busy}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Updating...
          {:else}
            Update & Add
          {/if}
        </Button>
      {:else if selectedFood}
        <!-- Existing food without edits - no form needed -->
        <Button
          type="button"
          onclick={handleAddFood}
          disabled={!canSubmit || isSubmitting || submitting}
        >
          {isSubmitting || submitting ? "Adding..." : "Add Food"}
        </Button>
      {:else}
        <!-- No selection yet -->
        <Button type="button" disabled>Add Food</Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
