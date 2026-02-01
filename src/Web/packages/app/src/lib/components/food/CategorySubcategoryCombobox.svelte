<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Check, ChevronsUpDown, Plus, SquarePlus } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { tick } from "svelte";

  interface Props {
    /** Current category value */
    category: string;
    /** Current subcategory value */
    subcategory: string;
    /** Map of categories to their subcategories */
    categories: Record<string, Record<string, boolean>>;
    /** Callback when category changes */
    onCategoryChange: (category: string) => void;
    /** Callback when subcategory changes */
    onSubcategoryChange: (subcategory: string) => void;
    /** Callback when a new category is created */
    onCategoryCreate?: (category: string) => void;
    /** Callback when a new subcategory is created */
    onSubcategoryCreate?: (category: string, subcategory: string) => void;
    /** Whether the combobox is disabled */
    disabled?: boolean;
    /** CSS class for the trigger button */
    class?: string;
  }

  let {
    category = $bindable(),
    subcategory = $bindable(),
    categories,
    onCategoryChange,
    onSubcategoryChange,
    onCategoryCreate,
    onSubcategoryCreate,
    disabled = false,
    class: className,
  }: Props = $props();

  // Combobox state
  let comboboxOpen = $state(false);
  let categorySelectionOpen = $state(false);
  let comboboxTriggerRef = $state<HTMLButtonElement | null>(null);
  let categorySelectionTriggerRef = $state<HTMLButtonElement | null>(null);

  // Search values
  let searchValue = $state("");
  let pendingSubcategoryName = $state("");

  // Get all categories as array
  let allCategories = $derived(Object.keys(categories));

  // Display label
  let displayLabel = $derived.by(() => {
    if (category && subcategory) {
      return `${category} > ${subcategory}`;
    } else if (category) {
      return category;
    } else {
      return "Select category/subcategory...";
    }
  });

  function closeComboboxAndFocus() {
    comboboxOpen = false;
    tick().then(() => comboboxTriggerRef?.focus());
  }

  function selectCategory(cat: string) {
    category = cat;
    subcategory = "";
    onCategoryChange(cat);
    onSubcategoryChange("");
    searchValue = "";
    closeComboboxAndFocus();
  }

  function selectSubcategory(cat: string, sub: string) {
    category = cat;
    subcategory = sub;
    onCategoryChange(cat);
    onSubcategoryChange(sub);
    searchValue = "";
    closeComboboxAndFocus();
  }

  function selectCategorySubcategory(value: string) {
    if (value === "") {
      // Clear selection
      selectCategory("");
    } else if (value.includes(" > ")) {
      const [cat, sub] = value.split(" > ");
      selectSubcategory(cat, sub);
    } else {
      selectCategory(value);
    }
  }

  function handleCreateNewCategory() {
    const categoryName = searchValue.trim();
    if (categoryName && !allCategories.includes(categoryName)) {
      onCategoryCreate?.(categoryName);
      category = categoryName;
      subcategory = "";
      onCategoryChange(categoryName);
      onSubcategoryChange("");
      comboboxOpen = false;
      searchValue = "";
    }
  }

  function handleCreateNewSubcategory() {
    pendingSubcategoryName = searchValue.trim();
    categorySelectionOpen = true;
    comboboxOpen = false;
  }

  function handleCategorySelectionForSubcategory(categoryName: string) {
    if (pendingSubcategoryName && categoryName) {
      onSubcategoryCreate?.(categoryName, pendingSubcategoryName);
      category = categoryName;
      subcategory = pendingSubcategoryName;
      onCategoryChange(categoryName);
      onSubcategoryChange(pendingSubcategoryName);
    }
    categorySelectionOpen = false;
    pendingSubcategoryName = "";
  }

  function handleCreateNewCategoryForSubcategory() {
    if (pendingSubcategoryName) {
      const newCategoryName = pendingSubcategoryName;
      onCategoryCreate?.(newCategoryName);
      category = newCategoryName;
      subcategory = "";
      onCategoryChange(newCategoryName);
      onSubcategoryChange("");
    }
    categorySelectionOpen = false;
    pendingSubcategoryName = "";
  }
</script>

<div class={className}>
  <Popover.Root bind:open={comboboxOpen}>
    <Popover.Trigger bind:ref={comboboxTriggerRef}>
      {#snippet child({ props })}
        <Button
          variant="outline"
          class="w-full justify-between font-normal"
          {...props}
          role="combobox"
          aria-expanded={comboboxOpen}
          {disabled}
        >
          <span class={!category ? "text-muted-foreground" : ""}>
            {displayLabel}
          </span>
          <ChevronsUpDown class="ml-2 size-4 shrink-0 opacity-50" />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-(--bits-popover-anchor-width) p-0">
      <Command.Root shouldFilter={false}>
        <Command.Input
          placeholder="Search categories and subcategories..."
          bind:value={searchValue}
        />
        <Command.List>
          <Command.Empty>No category found.</Command.Empty>

          <!-- Clear selection option -->
          <Command.Group>
            <Command.Item
              value=""
              onSelect={() => selectCategorySubcategory("")}
            >
              <Check
                class={cn(
                  "mr-2 size-4",
                  (category !== "" || subcategory !== "") && "text-transparent"
                )}
              />
              (none)
            </Command.Item>
          </Command.Group>

          <!-- Categories and their subcategories -->
          {#each allCategories.filter((cat) => !searchValue || cat
                .toLowerCase()
                .includes(searchValue.toLowerCase())) as cat}
            <Command.Group>
              <Command.Item
                value={cat}
                onSelect={() => selectCategorySubcategory(cat)}
              >
                <Check
                  class={cn(
                    "mr-2 size-4",
                    (category !== cat || subcategory !== "") &&
                      "text-transparent"
                  )}
                />
                <strong>{cat}</strong>
              </Command.Item>
              <!-- Subcategories for this category -->
              {#if categories[cat]}
                {#each Object.keys(categories[cat]).filter((sub) => !searchValue || sub
                      .toLowerCase()
                      .includes(searchValue.toLowerCase())) as sub}
                  <Command.Item
                    value={`${cat} > ${sub}`}
                    onSelect={() =>
                      selectCategorySubcategory(`${cat} > ${sub}`)}
                    class="pl-6"
                  >
                    <Check
                      class={cn(
                        "mr-2 size-4",
                        (category !== cat || subcategory !== sub) &&
                          "text-transparent"
                      )}
                    />
                    {sub}
                  </Command.Item>
                {/each}
              {/if}
            </Command.Group>

            <!-- Separator between categories -->
            {#if cat !== allCategories[allCategories.length - 1]}
              <Command.Separator />
            {/if}
          {/each}

          <!-- Create new category or subcategory options -->
          {#if searchValue && searchValue.trim()}
            {@const searchTerm = searchValue.trim()}
            {@const hasMatchingCategory = allCategories.some((cat) =>
              cat.toLowerCase().includes(searchTerm.toLowerCase())
            )}
            {@const hasMatchingSubcategory = allCategories.some(
              (cat) =>
                categories[cat] &&
                Object.keys(categories[cat]).some((sub) =>
                  sub.toLowerCase().includes(searchTerm.toLowerCase())
                )
            )}
            {#if !hasMatchingCategory && !hasMatchingSubcategory}
              <Command.Separator />
              <Command.Group>
                <Command.Item
                  value={`create-category-${searchTerm}`}
                  onSelect={handleCreateNewCategory}
                >
                  <Plus class="mr-2 size-4" />
                  Create category "{searchTerm}"
                </Command.Item>

                <Command.Item
                  value={`create-subcategory-${searchTerm}`}
                  onSelect={handleCreateNewSubcategory}
                  class="pl-0"
                >
                  <SquarePlus class="mr-2 size-4" />
                  Create subcategory "{searchTerm}"
                </Command.Item>
              </Command.Group>
            {/if}
          {/if}
        </Command.List>
      </Command.Root>
    </Popover.Content>
  </Popover.Root>

  <!-- Category Selection Popup for new subcategories -->
  <Popover.Root bind:open={categorySelectionOpen}>
    <Popover.Trigger bind:ref={categorySelectionTriggerRef} class="hidden">
      {#snippet child({ props })}
        <Button {...props}>Hidden trigger</Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-80 p-0">
      <Command.Root>
        <Command.Input placeholder="Search categories..." />
        <Command.List>
          <Command.Empty>No category found.</Command.Empty>
          <Command.Separator />
          <Command.Group>
            <Command.Item
              value="Create new category"
              onSelect={handleCreateNewCategoryForSubcategory}
            >
              <Plus class="mr-2 size-4" />
              Create "{pendingSubcategoryName}" as new category
            </Command.Item>
          </Command.Group>
          <Command.Separator />
          <Command.Group>
            {#each allCategories as cat}
              <Command.Item
                value={cat}
                onSelect={() => handleCategorySelectionForSubcategory(cat)}
              >
                <Check class={cn("mr-2 size-4", "text-transparent")} />
                Add to {cat}
              </Command.Item>
            {/each}
          </Command.Group>
        </Command.List>
      </Command.Root>
    </Popover.Content>
  </Popover.Root>
</div>
