<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Check,
    ChevronsUpDown,
    Plus,
    Star,
    Clock,
    ChevronDown,
    Scale,
    FileText,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { tick } from "svelte";
  import { toast } from "svelte-sonner";
  import {
    CarbIntakeFoodInputMode,
    type Food,
    type CarbIntakeFoodRequest,
  } from "$lib/api";
  import {
    getAllFoods,
    createNewFood,
    updateExistingFood,
  } from "$api/treatment-foods.remote";
  import {
    getFavorites as getFavoriteFoods,
    addFavorite as addFavoriteFood,
    removeFavorite as removeFavoriteFood,
    getRecentFoods,
  } from "$api/generated/foods.generated.remote";
  import { CategorySubcategoryCombobox } from "$lib/components/food";

  interface Props {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    onSubmit: (request: CarbIntakeFoodRequest) => void;
    /** Total carbs from the treatment */
    totalCarbs?: number;
    /** Remaining unspecified carbs */
    unspecifiedCarbs?: number;
  }

  let {
    open = $bindable(),
    onOpenChange,
    onSubmit,
    totalCarbs = 0,
    unspecifiedCarbs = 0,
  }: Props = $props();

  // Food lists
  let favorites = $state<Food[]>([]);
  let recents = $state<Food[]>([]);
  let allFoods = $state<Food[]>([]);

  // Combobox state
  let searchQuery = $state("");

  // Unit combobox state
  let unitOpen = $state(false);
  let unitTriggerRef = $state<HTMLButtonElement>(null!);

  // GI combobox state
  let giOpen = $state(false);
  let giTriggerRef = $state<HTMLButtonElement>(null!);

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
  let timeOffsetMinutes = $state<number | undefined>(0);
  let note = $state("");

  // Track which field was last edited
  let lastEditedField = $state<"portions" | "carbs">("portions");

  // Collapsible state for food details
  let nutritionDetailsOpen = $state(false);

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
  let isSaving = $state(false);

  // Constants
  const foodUnits = ["g", "ml", "pcs", "oz"];
  const giOptions = [
    { value: 1, label: "Low" },
    { value: 2, label: "Medium" },
    { value: 3, label: "High" },
  ];

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

  // Derived: filtered foods based on search
  const filteredFoods = $derived.by(() => {
    if (!searchQuery.trim()) return [];
    const query = searchQuery.trim().toLowerCase();
    return allFoods.filter((food) => {
      const name = food.name?.toLowerCase() ?? "";
      const category = food.category?.toLowerCase() ?? "";
      const subcategory = food.subcategory?.toLowerCase() ?? "";
      return (
        name.includes(query) ||
        category.includes(query) ||
        subcategory.includes(query)
      );
    });
  });

  // Derived: check if search matches any existing food name exactly
  const hasExactMatch = $derived(
    allFoods.some(
      (f) => f.name?.toLowerCase() === searchQuery.trim().toLowerCase()
    )
  );

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

  // Derived: display labels
  const selectedUnitLabel = $derived(foodUnit || "Select unit...");
  const selectedGiLabel = $derived(
    giOptions.find((opt) => opt.value === foodGi)?.label || "Select GI..."
  );

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
          getAllFoods(),
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
    isSaving = false;
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

  function buildFoodRecord(): Omit<Food, "_id"> & { _id?: string } {
    return {
      _id: selectedFood?._id,
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

  async function handleAddFood() {
    if (!selectedFood?._id) return;

    const request: CarbIntakeFoodRequest = {
      foodId: selectedFood._id,
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

    isSubmitting = true;
    onSubmit(request);
    isSubmitting = false;
  }

  async function handleUpdate() {
    if (!selectedFood?._id) return;

    isSaving = true;
    try {
      const foodRecord = buildFoodRecord();
      await updateExistingFood(foodRecord as any);

      // Update local state
      const idx = allFoods.findIndex((f) => f._id === selectedFood?._id);
      if (idx !== -1) {
        allFoods[idx] = { ...allFoods[idx], ...foodRecord };
      }

      // Update originalFood to reflect saved state
      originalFood = { ...selectedFood, ...foodRecord };

      toast.success("Food updated successfully");

      // Now add the food to treatment
      await handleAddFood();
    } catch (err) {
      console.error("Failed to update food:", err);
      toast.error("Failed to update food");
    } finally {
      isSaving = false;
    }
  }

  async function handleSaveAsNew() {
    if (!foodName.trim()) {
      toast.error("Please enter a food name");
      return;
    }

    isSaving = true;
    try {
      const foodRecord = buildFoodRecord();
      delete foodRecord._id;

      const result = await createNewFood(foodRecord as any);

      if (result.success && result.record) {
        // Add to allFoods
        const newFood = result.record as Food;
        allFoods = [...allFoods, newFood];

        // Select the new food
        selectedFood = newFood;
        originalFood = { ...newFood };

        toast.success("Food created successfully");

        // Now add the food to treatment
        const request: CarbIntakeFoodRequest = {
          foodId: newFood._id!,
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

        onSubmit(request);
      } else {
        throw new Error("Failed to create food");
      }
    } catch (err) {
      console.error("Failed to create food:", err);
      toast.error("Failed to create food");
    } finally {
      isSaving = false;
    }
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
    onSubmit(request);
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
      !isSaving
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
      <div class="space-y-2">
        <Label>Food</Label>

        <Command.Root shouldFilter={false}>
          <Command.Input
            placeholder="Search foods..."
            bind:value={searchQuery}
          />
          <Command.List class="max-h-[300px]">
            {#if isLoading}
              <Command.Empty>Loading foods...</Command.Empty>
            {:else if !searchQuery.trim()}
              <!-- Show favorites and recents when no search -->
              {#if favorites.length > 0}
                <Command.Group>
                  <div
                    class="px-2 py-1.5 text-xs font-medium text-muted-foreground flex items-center gap-1"
                  >
                    <Star class="h-3 w-3 text-yellow-500" />
                    Favorites
                  </div>
                  {#each favorites as food (food._id)}
                    <Command.Item
                      value={food._id}
                      onSelect={() => selectFood(food)}
                      class="cursor-pointer"
                    >
                      <Check
                        class={cn(
                          "mr-2 h-4 w-4",
                          selectedFood?._id === food._id
                            ? "opacity-100"
                            : "opacity-0"
                        )}
                      />
                      <div class="flex-1">
                        <span>{food.name}</span>
                        <span class="ml-2 text-xs text-muted-foreground">
                          {food.carbs}g carbs
                        </span>
                      </div>
                    </Command.Item>
                  {/each}
                </Command.Group>
              {/if}

              {#if recents.length > 0}
                <Command.Group>
                  <div
                    class="px-2 py-1.5 text-xs font-medium text-muted-foreground flex items-center gap-1"
                  >
                    <Clock class="h-3 w-3 text-sky-500" />
                    Recent
                  </div>
                  {#each recents as food (food._id)}
                    <Command.Item
                      value={food._id}
                      onSelect={() => selectFood(food)}
                      class="cursor-pointer"
                    >
                      <Check
                        class={cn(
                          "mr-2 h-4 w-4",
                          selectedFood?._id === food._id
                            ? "opacity-100"
                            : "opacity-0"
                        )}
                      />
                      <div class="flex-1">
                        <span>{food.name}</span>
                        <span class="ml-2 text-xs text-muted-foreground">
                          {food.carbs}g carbs
                        </span>
                      </div>
                    </Command.Item>
                  {/each}
                </Command.Group>
              {/if}

              {#if favorites.length === 0 && recents.length === 0}
                <Command.Empty>
                  Type to search foods or create a new one.
                </Command.Empty>
              {/if}
            {:else}
              <!-- Show filtered results and create option -->
              {#if filteredFoods.length > 0}
                <Command.Group>
                  {#each filteredFoods as food (food._id)}
                    <Command.Item
                      value={food._id}
                      onSelect={() => selectFood(food)}
                      class="cursor-pointer"
                    >
                      <Check
                        class={cn(
                          "mr-2 h-4 w-4",
                          selectedFood?._id === food._id
                            ? "opacity-100"
                            : "opacity-0"
                        )}
                      />
                      <div class="flex-1">
                        <span>{food.name}</span>
                        <span class="ml-2 text-xs text-muted-foreground">
                          {food.carbs}g carbs
                        </span>
                      </div>
                      {#if food.category}
                        <Badge variant="outline" class="text-xs">
                          {food.category}
                        </Badge>
                      {/if}
                    </Command.Item>
                  {/each}
                </Command.Group>
              {/if}

              <!-- Create new and log without saving options when searching -->
              {#if searchQuery.trim()}
                <Command.Group>
                  {#if !hasExactMatch}
                    <Command.Item
                      value="__create_new__"
                      onSelect={startCreateNew}
                      class="cursor-pointer text-primary"
                    >
                      <Plus class="mr-2 h-4 w-4" />
                      Create "{searchQuery.trim()}"
                    </Command.Item>
                  {/if}
                  <Command.Item
                    value="__log_without_saving__"
                    onSelect={startLogWithoutSaving}
                    class="cursor-pointer text-muted-foreground"
                  >
                    <FileText class="mr-2 h-4 w-4" />
                    Log without saving food
                  </Command.Item>
                </Command.Group>
              {/if}

              {#if filteredFoods.length === 0 && hasExactMatch}
                <Command.Empty>No additional matches found.</Command.Empty>
              {/if}
            {/if}
          </Command.List>
        </Command.Root>
      </div>

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
        <div class="border-t pt-4 space-y-4">
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
                onCategoryCreate={(_cat) => {
                  // Category will be created when food is saved
                }}
                onSubcategoryCreate={(_cat, _sub) => {
                  // Subcategory will be created when food is saved
                }}
              />
            </div>
          </div>

          <!-- Quick summary of selected food -->
          {#if selectedFood && !isCreatingNew}
            <div
              class="flex items-center justify-between rounded-lg border bg-muted/30 p-3"
            >
              <div class="flex items-center gap-4">
                <div class="text-sm">
                  <span class="font-medium">{foodPortion}{foodUnit}</span>
                  <span class="text-muted-foreground">=</span>
                  <span
                    class="font-semibold text-green-600 dark:text-green-400"
                  >
                    {foodCarbs}g carbs
                  </span>
                </div>
                {#if foodFat > 0 || foodProtein > 0}
                  <div class="text-xs text-muted-foreground">
                    {foodFat}g fat • {foodProtein}g protein
                  </div>
                {/if}
              </div>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onclick={() => selectedFood && toggleFavorite(selectedFood)}
              >
                <Star
                  class="h-4 w-4 {favorites.some(
                    (fav) => fav._id === selectedFood?._id
                  )
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
              <div class="grid gap-4 md:grid-cols-4">
                <div class="space-y-2">
                  <Label for="food-portion">Portion Size</Label>
                  <Input
                    id="food-portion"
                    type="number"
                    bind:value={foodPortion}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="food-unit">Unit</Label>
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
                          <ChevronsUpDown
                            class="ml-2 size-4 shrink-0 opacity-50"
                          />
                        </Button>
                      {/snippet}
                    </Popover.Trigger>
                    <Popover.Content
                      class="w-(--bits-popover-anchor-width) p-0"
                    >
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
                  <Input id="food-carbs" type="number" bind:value={foodCarbs} />
                </div>
                <div class="space-y-2">
                  <Label for="food-gi">GI</Label>
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
                          <ChevronsUpDown
                            class="ml-2 size-4 shrink-0 opacity-50"
                          />
                        </Button>
                      {/snippet}
                    </Popover.Trigger>
                    <Popover.Content
                      class="w-(--bits-popover-anchor-width) p-0"
                    >
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
                                    foodGi !== option.value &&
                                      "text-transparent"
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
              <div class="grid gap-4 md:grid-cols-3">
                <div class="space-y-2">
                  <Label for="food-fat">Fat (g)</Label>
                  <Input id="food-fat" type="number" bind:value={foodFat} />
                </div>
                <div class="space-y-2">
                  <Label for="food-protein">Protein (g)</Label>
                  <Input
                    id="food-protein"
                    type="number"
                    bind:value={foodProtein}
                  />
                </div>
                <div class="space-y-2">
                  <Label for="food-energy">Energy (kJ)</Label>
                  <Input
                    id="food-energy"
                    type="number"
                    bind:value={foodEnergy}
                  />
                </div>
              </div>
            </Collapsible.Content>
          </Collapsible.Root>
        </div>
      {/if}

      <!-- Treatment request fields - shared between food selection and log without saving -->
      {#if showForm || showSimpleCarbsForm}
        <div class="border-t pt-4 space-y-4">
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
          <div class="grid gap-4 md:grid-cols-3">
            {#if !showSimpleCarbsForm}
              <div class="space-y-2">
                <Label for="portions">Portions</Label>
                <Input
                  id="portions"
                  type="number"
                  step="0.1"
                  min="0"
                  value={portions}
                  oninput={(e) =>
                    handlePortionsChange(
                      parseFloat(e.currentTarget.value) || 0
                    )}
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
                oninput={(e) =>
                  handleCarbsChange(parseFloat(e.currentTarget.value) || 0)}
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
          {#if unspecifiedCarbs > 0 && (foodCarbs > 0 || showSimpleCarbsForm)}
            <Button
              type="button"
              variant="outline"
              size="sm"
              class="w-full"
              onclick={scaleToFit}
            >
              <Scale class="mr-2 h-4 w-4" />
              Scale to fill remaining {unspecifiedCarbs}g
            </Button>
          {/if}

          <div class="space-y-2">
            <Label for="note">
              {showSimpleCarbsForm ? "Description" : "Note (optional)"}
            </Label>
            <Input
              id="note"
              bind:value={note}
              placeholder={showSimpleCarbsForm
                ? "What did you eat?"
                : "Add a note..."}
            />
          </div>
        </div>
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
          disabled={!canSubmit || isSubmitting}
        >
          {isSubmitting ? "Logging..." : "Log Carbs"}
        </Button>
      {:else if isCreatingNew}
        <!-- Creating new food -->
        <Button
          type="button"
          onclick={handleSaveAsNew}
          disabled={!canSubmit || isSaving}
        >
          {isSaving ? "Saving..." : "Save & Add"}
        </Button>
      {:else if selectedFood && hasEdits}
        <!-- Existing food with edits -->
        <Button
          type="button"
          variant="outline"
          onclick={handleSaveAsNew}
          disabled={!canSubmit || isSaving}
        >
          {isSaving ? "Saving..." : "Save as New"}
        </Button>
        <Button
          type="button"
          onclick={handleUpdate}
          disabled={!canSubmit || isSaving}
        >
          {isSaving ? "Updating..." : "Update & Add"}
        </Button>
      {:else if selectedFood}
        <!-- Existing food without edits -->
        <Button
          type="button"
          onclick={handleAddFood}
          disabled={!canSubmit || isSubmitting}
        >
          {isSubmitting ? "Adding..." : "Add Food"}
        </Button>
      {:else}
        <!-- No selection yet -->
        <Button type="button" disabled>Add Food</Button>
      {/if}
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
