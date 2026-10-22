<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import Check from "@lucide/svelte/icons/check";
  import Plus from "@lucide/svelte/icons/plus";
  import ChevronsUpDown from "@lucide/svelte/icons/chevrons-up-down";
  import { cn } from "$lib/utils";
  import { labelFor } from "$lib/components/ui/enum-value";
  import type { DeviceCatalogEntry } from "$api";
  import { deviceCategoryLabels } from "./labels";

  interface Props {
    id: string;
    /** Every known model, across all categories. */
    catalog: DeviceCatalogEntry[];
    /** The device's model name as it will be saved. */
    value: string;
    /** Catalog id of the picked model; empty for a name typed in. */
    catalogId: string;
    /** A catalog model was picked: fill in everything it knows. */
    onPick: (entry: DeviceCatalogEntry) => void;
    /** A name the catalog does not list was entered. */
    onCustom: (name: string) => void;
  }

  let { id, catalog, value, catalogId, onPick, onCustom }: Props = $props();

  let open = $state(false);
  let search = $state("");

  const matches = $derived.by(() => {
    const q = search.trim().toLowerCase();
    if (!q) return catalog;
    return catalog.filter((e) =>
      `${e.manufacturer} ${e.name}`.toLowerCase().includes(q),
    );
  });

  const byCategory = $derived(
    [...new Set(matches.map((e) => e.category ?? ""))].map(
      (category) => [category, matches.filter((e) => (e.category ?? "") === category)] as const,
    ),
  );

  const typed = $derived(search.trim());
  const typedIsListed = $derived(
    catalog.some((e) => e.name?.toLowerCase() === typed.toLowerCase()),
  );

  function pick(entry: DeviceCatalogEntry) {
    onPick(entry);
    open = false;
    search = "";
  }

  function useTyped() {
    onCustom(typed);
    open = false;
    search = "";
  }
</script>

<Popover.Root bind:open>
  <Popover.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        variant="combobox"
        role="combobox"
        aria-expanded={open}
        class="w-full justify-between"
        {...props}
        {id}
      >
        {#if value}
          <span class="truncate">{value}</span>
        {:else}
          <span class="text-muted-foreground">Search devices...</span>
        {/if}
        <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-[320px] p-0" align="start">
    <Command.Root shouldFilter={false}>
      <Command.Input placeholder="Search or type a device name..." bind:value={search} />
      <Command.List>
        {#if typed && !typedIsListed}
          <Command.Group>
            <Command.Item value="custom:{typed}" onSelect={useTyped} class="cursor-pointer">
              <Plus class="mr-2 h-4 w-4" />
              Use "{typed}"
            </Command.Item>
          </Command.Group>
        {/if}
        {#each byCategory as [category, entries] (category)}
          <Command.Group heading={labelFor(deviceCategoryLabels, category) ?? category}>
            {#each entries as entry (entry.id)}
              <Command.Item
                value={entry.id ?? ""}
                onSelect={() => pick(entry)}
                class="cursor-pointer"
              >
                <Check
                  class={cn("mr-2 h-4 w-4", catalogId === entry.id ? "opacity-100" : "opacity-0")}
                />
                <span>{entry.name}</span>
                <span class="ml-auto text-xs text-muted-foreground">{entry.manufacturer}</span>
              </Command.Item>
            {/each}
          </Command.Group>
        {/each}
        {#if matches.length === 0 && !typed}
          <Command.Empty>No devices found.</Command.Empty>
        {/if}
      </Command.List>
    </Command.Root>
  </Popover.Content>
</Popover.Root>
