<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
  import * as Command from "$lib/components/ui/command";
  import { Filter, X, Check } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import type { Component } from "svelte";

  interface Option {
    value: string;
    label: string;
    icon?: Component;
  }

  interface Props {
    label: string;
    options: Option[];
    selected: string[];
    searchable?: boolean;
    searchPlaceholder?: string;
    onToggle: (value: string) => void;
    onClear: () => void;
  }

  let { label, options, selected, searchable = false, searchPlaceholder = "Search...", onToggle, onClear }: Props = $props();

  let open = $state(false);
  let search = $state("");

  let filteredOptions = $derived.by(() => {
    if (!searchable || !search.trim()) return options;
    const searchLower = search.toLowerCase();
    return options.filter((opt) => opt.label.toLowerCase().includes(searchLower));
  });
</script>

<Popover.Root bind:open>
  <Popover.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        variant="ghost"
        size="sm"
        class="-ml-3 h-8 data-[state=open]:bg-accent gap-1"
        {...props}
      >
        {label}
        {#if selected.length > 0}
          <Badge variant="secondary" class="ml-1 h-5 px-1 text-xs">
            {selected.length}
          </Badge>
        {/if}
        <Filter class="ml-1 h-3 w-3 opacity-50" />
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content
    class={cn("p-0", searchable ? "w-[220px]" : "w-[200px]")}
    align="start"
  >
    <Command.Root shouldFilter={false}>
      {#if searchable}
        <Command.Input
          placeholder={searchPlaceholder}
          bind:value={search}
        />
      {/if}
      <Command.List class={searchable ? "max-h-[200px]" : ""}>
        {#if searchable && filteredOptions.length === 0}
          <Command.Empty>No options found.</Command.Empty>
        {/if}
        <Command.Group>
          {#each filteredOptions as option}
            <Command.Item
              value={option.value}
              onSelect={() => onToggle(option.value)}
              class="cursor-pointer"
            >
              <div
                class={cn(
                  "mr-2 h-4 w-4 border rounded flex items-center justify-center",
                  selected.includes(option.value)
                    ? "bg-primary border-primary"
                    : "border-muted"
                )}
              >
                {#if selected.includes(option.value)}
                  <Check class="h-3 w-3 text-primary-foreground" />
                {/if}
              </div>
              {#if option.icon}
                <option.icon class="mr-2 h-4 w-4" />
              {/if}
              <span class={searchable ? "truncate" : ""}>{option.label}</span>
            </Command.Item>
          {/each}
        </Command.Group>
      </Command.List>
      {#if selected.length > 0}
        <div class="border-t p-2">
          <Button
            variant="ghost"
            size="sm"
            class="w-full"
            onclick={onClear}
          >
            <X class="mr-2 h-3 w-3" />
            Clear filter
          </Button>
        </div>
      {/if}
    </Command.Root>
  </Popover.Content>
</Popover.Root>
