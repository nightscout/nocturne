<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { toastSaved, useToastSubmission } from "#lib/forms/index.js";
  import type { Food } from "#lib/api/index.js";
  import FoodFields from "./FoodFields.svelte";
  import { DEFAULT_PORTION, DEFAULT_UNIT, DEFAULT_GI, type FoodCategoryMap } from "./food-constants";
  import { createFood, updateFood } from "#lib/api/generated/foods.generated.remote.js";

  interface Props {
    /** Whether the dialog is open */
    open: boolean;
    /** Callback when open state changes */
    onOpenChange: (open: boolean) => void;
    /** Initial food data to edit (optional) */
    initialFood?: Food | null;
    /** Categories map from parent */
    categories: FoodCategoryMap;
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

  let draft = $state<Food>(emptyDraft());

  const save = useToastSubmission("Failed to save food");

  $effect(() => {
    if (open) {
      draft = initialFood ? { ...initialFood } : emptyDraft();
    }
  });

  function emptyDraft(): Food {
    return {
      type: "food",
      name: "",
      category: "",
      subcategory: "",
      portion: DEFAULT_PORTION,
      unit: DEFAULT_UNIT,
      gi: DEFAULT_GI,
    };
  }

  function handleCancel() {
    onOpenChange(false);
  }

  const editingFoodId = $derived(draft._id);
  const canSave = $derived((draft.name ?? "").trim() !== "" && !save.busy);
  const dialogTitle = $derived(editingFoodId ? "Edit food" : "Add food");
  const saveButtonLabel = $derived(
    save.busy ? "Saving..." : editingFoodId ? "Update" : "Create"
  );

  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    if (!canSave) return;

    await save.run(async () => {
      const payload: Food = { ...draft, type: "food" };

      const result = editingFoodId
        ? await updateFood({ foodId: editingFoodId, request: payload })
        : await createFood(payload);

      toastSaved(editingFoodId ? "Food updated successfully" : "Food created successfully");
      onSave?.(result);
      onOpenChange(false);
    });
  }
</script>

<Dialog.Root bind:open {onOpenChange}>
  <Dialog.Content class="@container max-w-2xl max-h-[90vh] overflow-y-auto">
    <Dialog.Header>
      <Dialog.Title>{dialogTitle}</Dialog.Title>
      <Dialog.Description>
        {editingFoodId
          ? "Update the food's nutritional information."
          : "Create a new food with nutritional information."}
      </Dialog.Description>
    </Dialog.Header>

    <form onsubmit={handleSubmit}>
      {#if editingFoodId}
        <input type="hidden" name="_id" value={editingFoodId} />
      {/if}
      <input type="hidden" name="type" value="food" />
      <FoodFields bind:draft {categories} {onCategoryCreate} {onSubcategoryCreate} />

      <Dialog.Footer class="mt-4 gap-2">
        <Button type="button" variant="outline" onclick={handleCancel}>
          Cancel
        </Button>
        <Button type="submit" disabled={!canSave}>
          {saveButtonLabel}
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
