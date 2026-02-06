<script lang="ts">
  import type { NoteChecklistItem } from "$lib/api/generated/nocturne-api-client";
  import { cn, randomUUID } from "$lib/utils";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Input } from "$lib/components/ui/input";
  import { Button } from "$lib/components/ui/button";
  import Plus from "lucide-svelte/icons/plus";
  import X from "lucide-svelte/icons/x";
  import GripVertical from "lucide-svelte/icons/grip-vertical";

  interface Props {
    items?: NoteChecklistItem[];
    onToggle?: (item: NoteChecklistItem) => void;
    readonly?: boolean;
    class?: string;
  }

  let {
    items = $bindable([]),
    onToggle,
    readonly = false,
    class: className,
  }: Props = $props();

  let newItemText = $state("");

  function handleToggle(item: NoteChecklistItem) {
    const index = items.findIndex((i) => i.id === item.id);
    if (index !== -1) {
      items[index] = {
        ...items[index],
        isCompleted: !items[index].isCompleted,
        completedAt: !items[index].isCompleted ? new Date() : undefined,
      };
      onToggle?.(items[index]);
    }
  }

  function addItem() {
    if (!newItemText.trim()) return;

    const newItem: NoteChecklistItem = {
      id: randomUUID(),
      text: newItemText.trim(),
      isCompleted: false,
      sortOrder: items.length,
    };

    items = [...items, newItem];
    newItemText = "";
  }

  function removeItem(item: NoteChecklistItem) {
    items = items.filter((i) => i.id !== item.id);
  }

  function handleKeyDown(event: KeyboardEvent) {
    if (event.key === "Enter") {
      event.preventDefault();
      addItem();
    }
  }

  const completedCount = $derived(items.filter((i) => i.isCompleted).length);
  const totalCount = $derived(items.length);
</script>

<div class={cn("space-y-2", className)}>
  {#if items.length > 0}
    <div class="space-y-1">
      {#each items as item (item.id)}
        <div
          class={cn(
            "group flex items-center gap-2 rounded-md px-2 py-1.5 transition-colors",
            !readonly && "hover:bg-muted/50"
          )}
        >
          {#if !readonly}
            <GripVertical class="size-4 cursor-grab text-muted-foreground opacity-0 group-hover:opacity-100" />
          {/if}

          <Checkbox
            checked={item.isCompleted}
            onCheckedChange={() => handleToggle(item)}
            disabled={readonly}
          />

          <span
            class={cn(
              "flex-1 text-sm",
              item.isCompleted && "text-muted-foreground line-through"
            )}
          >
            {item.text}
          </span>

          {#if !readonly}
            <Button
              variant="ghost"
              size="icon"
              class="size-6 opacity-0 group-hover:opacity-100"
              onclick={() => removeItem(item)}
            >
              <X class="size-3" />
              <span class="sr-only">Remove item</span>
            </Button>
          {/if}
        </div>
      {/each}
    </div>
  {/if}

  {#if !readonly}
    <div class="flex items-center gap-2">
      <Input
        type="text"
        placeholder="Add checklist item..."
        bind:value={newItemText}
        onkeydown={handleKeyDown}
        class="h-8 text-sm"
      />
      <Button
        variant="outline"
        size="icon"
        class="size-8 shrink-0"
        onclick={addItem}
        disabled={!newItemText.trim()}
      >
        <Plus class="size-4" />
        <span class="sr-only">Add item</span>
      </Button>
    </div>
  {/if}

  {#if totalCount > 0}
    <div class="text-xs text-muted-foreground">
      {completedCount}/{totalCount} completed
    </div>
  {/if}
</div>
