<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Check, ChevronsUpDown, Plus } from "lucide-svelte";
  import {
    getEventTypeStyle,
    TREATMENT_CATEGORIES,
  } from "$lib/constants/treatment-categories";
  import { cn } from "$lib/utils";

  interface Props {
    /** Currently selected event type */
    value?: string;
    /** Callback when an event type is selected */
    onSelect: (eventType: string) => void;
    /** Additional event types to include beyond the standard categories */
    additionalEventTypes?: string[];
    /** Placeholder text when no value is selected */
    placeholder?: string;
    /** Allow entering custom event types not in the list */
    allowCustom?: boolean;
    /** Show a "Create New" option at the top */
    showCreateNew?: boolean;
    /** Callback when "Create New" is clicked */
    onCreateNew?: () => void;
    /** Show a "None" option to clear the selection */
    showNone?: boolean;
    /** Disabled state */
    disabled?: boolean;
    /** CSS class for the trigger button */
    class?: string;
  }

  let {
    value = $bindable(),
    onSelect,
    additionalEventTypes = [],
    placeholder = "Select event type...",
    allowCustom = true,
    showCreateNew = false,
    onCreateNew,
    showNone = true,
    disabled = false,
    class: className,
  }: Props = $props();

  let popoverOpen = $state(false);
  let searchValue = $state("");

  // Get all known event types from categories + additional provided
  let allEventTypes = $derived.by(() => {
    const categoryTypes = Object.values(TREATMENT_CATEGORIES).flatMap(
      (cat) => cat.eventTypes as readonly string[]
    );
    const combined = new Set([...categoryTypes, ...additionalEventTypes]);
    return Array.from(combined).sort();
  });

  // Filter event types based on search
  let filteredEventTypes = $derived.by(() => {
    if (!searchValue.trim()) return allEventTypes;
    const search = searchValue.toLowerCase();
    return allEventTypes.filter((type) => type.toLowerCase().includes(search));
  });

  // Check if search value matches an existing type
  let isCustomValue = $derived(
    allowCustom &&
      searchValue.trim() &&
      !allEventTypes.some(
        (t) => t.toLowerCase() === searchValue.trim().toLowerCase()
      )
  );

  function selectEventType(type: string) {
    value = type;
    onSelect(type);
    popoverOpen = false;
    searchValue = "";
  }

  function handleCreateNew() {
    popoverOpen = false;
    searchValue = "";
    onCreateNew?.();
  }

  function clearSelection() {
    value = undefined;
    onSelect("");
    popoverOpen = false;
    searchValue = "";
  }

  let style = $derived(getEventTypeStyle(value ?? ""));
</script>

<Popover.Root bind:open={popoverOpen}>
  <Popover.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        variant="outline"
        role="combobox"
        aria-expanded={popoverOpen}
        class={cn("w-full justify-between font-normal", className)}
        {disabled}
        {...props}
      >
        {#if value}
          <span class={style.colorClass}>{value}</span>
        {:else}
          <span class="text-muted-foreground">{placeholder}</span>
        {/if}
        <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-[300px] p-0" align="start">
    <Command.Root shouldFilter={false}>
      <Command.Input
        placeholder="Search or enter event type..."
        bind:value={searchValue}
      />
      <Command.List>
        <Command.Empty>
          {#if isCustomValue}
            <button
              type="button"
              class="w-full p-2 text-left text-sm hover:bg-accent rounded"
              onclick={() => selectEventType(searchValue.trim())}
            >
              Use "{searchValue.trim()}" as custom type
            </button>
          {:else}
            No event types found.
          {/if}
        </Command.Empty>
        <Command.Group>
          {#if showCreateNew && onCreateNew}
            <Command.Item
              value="__create_new__"
              onSelect={handleCreateNew}
              class="cursor-pointer text-primary"
            >
              <Plus class="mr-2 h-4 w-4" />
              Create new event type...
            </Command.Item>
          {/if}
          {#if showNone}
            <Command.Item
              value="__none__"
              onSelect={clearSelection}
              class="cursor-pointer text-muted-foreground"
            >
              <Check
                class={cn("mr-2 h-4 w-4", !value ? "opacity-100" : "opacity-0")}
              />
              None (don't create event)
            </Command.Item>
          {/if}
          {#each filteredEventTypes as type}
            {@const typeStyle = getEventTypeStyle(type)}
            <Command.Item
              value={type}
              onSelect={() => selectEventType(type)}
              class="cursor-pointer"
            >
              <Check
                class={cn(
                  "mr-2 h-4 w-4",
                  value === type ? "opacity-100" : "opacity-0"
                )}
              />
              <span class={typeStyle.colorClass}>{type}</span>
            </Command.Item>
          {/each}
        </Command.Group>
      </Command.List>
    </Command.Root>
  </Popover.Content>
</Popover.Root>
