<script lang="ts">
  import * as Command from "#lib/components/ui/command/index.js";
  import { getRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { computeCommandScore } from "bits-ui";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { satisfiesScope } from "#lib/authorization/scopes.js";
  import {
    glucoseChartLookback,
    setColorScheme,
    getColorScheme,
    getGlucoseUnits,
    setGlucoseUnits,
  } from "#lib/stores/appearance-store.svelte.js";
  import {
    paletteItemsFor,
    paletteKeywordWords,
    paletteStatValues,
    groupMeta,
    type CommandPaletteItem,
    type CommandPaletteGroup,
  } from "./command-palette-items.svelte.js";
  import {
    pinnedItemIds,
    recentItemIds,
    togglePin,
    isPinned,
    recordRecent,
  } from "./command-palette-store.svelte";
  import CommandPaletteVitals from "./CommandPaletteVitals.svelte";
  import Star from "@lucide/svelte/icons/star";
  import { Button } from "#lib/components/ui/button/index.js";

  interface Props {
    open: boolean;
    /**
     * Whether the host resolves no tenant (the apex, or a reserved dashboard
     * slug).
     */
    tenantless?: boolean;
  }

  let { open = $bindable(false), tenantless = false }: Props = $props();

  let searchValue = $state("");
  let highlighted = $state("");
  let highlightedId: string | null = null;
  let restoreHighlightTo: string | null = null;

  // An item whose label changes, a toggled setting or a live stat, registers again under its new
  // label, and bits-ui then selects the first item, more than once. Until the person moves again,
  // a selection goes to the item that was highlighted, unless the search no longer renders it: its
  // new label may stop matching, and bits-ui draws no item whose score is zero.
  function setHighlighted(next: string) {
    const keptItem = visibleItems.find((item) => item.id === restoreHighlightTo);
    const kept =
      keptItem &&
      (!searchValue ||
        computeCommandScore(keptItem.label, searchValue, keptItem.keywords) > 0)
        ? keptItem.label
        : undefined;
    if (!kept) restoreHighlightTo = null;
    highlighted = kept ?? next;
    if (restoreHighlightTo === null) {
      highlightedId = visibleItems.find((item) => item.label === next)?.id ?? null;
    }
  }

  const realtimeStore = getRealtimeStore();

  const visibleItems = $derived(
    paletteItemsFor(
      tenantless,
      {
        grantedScopes: page.data.effectivePermissions ?? [],
        anonymous: !page.data.user,
      },
      paletteKeywordWords(),
      paletteStatValues(realtimeStore)
    ).filter(
      (item) =>
        (!item.scope ||
          satisfiesScope(page.data.effectivePermissions ?? [], item.scope)) &&
        (!item.platformAdmin || page.data.isPlatformAdmin) &&
        !(item.notForDemoSubject && page.data.refusedAsDemoSubject)
    )
  );

  const pinnedItems = $derived(
    pinnedItemIds.current
      .map((id) => visibleItems.find((item) => item.id === id))
      .filter((item): item is CommandPaletteItem => item != null)
  );

  const recentItems = $derived(
    recentItemIds.current
      .map((id) => visibleItems.find((item) => item.id === id))
      .filter((item): item is CommandPaletteItem => item != null)
      .filter((item) => !isPinned(item.id))
  );

  const showPinnedRecent = $derived(!searchValue);

  // Filter out pinned and recent items from main groups when they're shown separately
  const mainGroupItems = $derived.by(() => {
    if (!showPinnedRecent) return visibleItems;
    const excludeIds = new Set([
      ...pinnedItemIds.current,
      ...recentItemIds.current,
    ]);
    return visibleItems.filter((item) => !excludeIds.has(item.id));
  });

  const groupedItems = $derived.by(() => {
    const groups: CommandPaletteGroup[] = [];
    for (const item of mainGroupItems) {
      if (!groups.includes(item.group)) groups.push(item.group);
    }

    const meta = groupMeta();
    return groups
      .sort((a, b) => meta[a].order - meta[b].order)
      .map((group): [CommandPaletteGroup, CommandPaletteItem[]] => [
        group,
        mainGroupItems.filter((item) => item.group === group),
      ]);
  });

  function handleSelect(item: CommandPaletteItem) {
    recordRecent(item.id);

    if (item.href) {
      open = false;
      // eslint-disable-next-line svelte/no-navigation-without-resolve -- item.href is a literal in-app path from command-palette-items.svelte.ts
      goto(item.href);
    } else if (item.linkedHref) {
      open = false;
      // eslint-disable-next-line svelte/no-navigation-without-resolve -- item.linkedHref is a literal in-app path from command-palette-items.svelte.ts
      goto(item.linkedHref);
    } else {
      handleAction(item.id);
    }
  }

  function handleAction(itemId: string) {
    switch (itemId) {
      case "qs-dark-mode": {
        const current = getColorScheme();
        setColorScheme(current === "dark" ? "light" : "dark");
        break;
      }
      case "qs-glucose-units": {
        const current = getGlucoseUnits();
        setGlucoseUnits(current === "mg/dl" ? "mmol" : "mg/dl");
        break;
      }
      case "qs-chart-lookback": {
        const options = [2, 4, 6, 12, 24, 48];
        const current = glucoseChartLookback.current;
        const idx = options.indexOf(current);
        glucoseChartLookback.current = options[(idx + 1) % options.length];
        break;
      }
      case "action-add-treatment":
        open = false;
        goto(resolve('reports/treatments'));
        break;
      case "action-add-food":
        open = false;
        goto(resolve('food'));
        break;
      case "action-manual-sync":
        open = false;
        goto(resolve('settings/connectors'));
        break;
    }
  }

  const labelSignature = $derived(visibleItems.map((item) => item.label).join("|"));

  $effect.pre(() => {
    if (!open) return;
    void labelSignature;
    restoreHighlightTo = highlightedId;
  });

  $effect(() => {
    if (!open) {
      searchValue = "";
      restoreHighlightTo = null;
      highlightedId = null;
    }
  });
</script>

{#snippet itemBody(item: CommandPaletteItem)}
  {#if item.icon}
    {@const Icon = item.icon}
    <Icon class="mr-2 h-4 w-4" />
  {/if}
  <div class="flex flex-1 flex-col">
    <span>{item.label}</span>
    {#if item.description}
      <span class="text-xs text-muted-foreground">{item.description}</span>
    {/if}
  </div>
{/snippet}

{#snippet commandItem(item: CommandPaletteItem, pinAlwaysVisible: boolean)}
  {@const pinned = isPinned(item.id)}
  <!-- The pin sits beside the item, not in it: a button inside an option or link fires both. The row hides with its item when a search filters the item out. -->
  <div
    class="group flex items-center rounded-sm pr-2 not-has-data-[slot=command-item]:hidden has-aria-selected:bg-accent has-aria-selected:text-accent-foreground"
  >
    {#if item.href}
      <Command.LinkItem
        class="flex-1"
        href={item.href}
        value={item.label}
        keywords={item.keywords}
        onSelect={() => handleSelect(item)}
      >
        {@render itemBody(item)}
      </Command.LinkItem>
    {:else}
      <Command.Item
        class="flex-1"
        value={item.label}
        keywords={item.keywords}
        onSelect={() => handleSelect(item)}
      >
        {@render itemBody(item)}
      </Command.Item>
    {/if}
    <Button
      variant="ghost-muted"
      size="icon-2xs"
      reveal={!(pinAlwaysVisible || pinned)}
      aria-label={pinned ? `Unpin ${item.label}` : `Pin ${item.label}`}
      onclick={() => togglePin(item.id)}
    >
      <Star
        class="size-3.5 {pinned
          ? 'fill-current text-favorite'
          : 'text-muted-foreground'}"
      />
    </Button>
  </div>
{/snippet}

<svelte:window
  onkeydowncapture={() => (restoreHighlightTo = null)}
  onpointermovecapture={() => (restoreHighlightTo = null)}
  oninputcapture={() => (restoreHighlightTo = null)}
/>

<Command.Dialog bind:open bind:value={() => highlighted, setHighlighted}>
  <Command.Input placeholder="Search commands..." bind:value={searchValue} />

  <!-- The vitals strip reads one tenant's live glucose. -->
  {#if !tenantless}
    <CommandPaletteVitals />
  {/if}

  <Command.List class="max-h-[400px]">
    <Command.Empty>No results found.</Command.Empty>

    {#if showPinnedRecent && pinnedItems.length > 0}
      <Command.Group heading="Pinned">
        {#each pinnedItems as item (item.id)}
          {@render commandItem(item, true)}
        {/each}
      </Command.Group>
    {/if}

    {#if showPinnedRecent && recentItems.length > 0}
      <Command.Group heading="Recent">
        {#each recentItems as item (item.id)}
          {@render commandItem(item, false)}
        {/each}
      </Command.Group>
    {/if}

    {#each groupedItems as [group, groupItems] (group)}
      <Command.Group heading={groupMeta()[group].label}>
        {#each groupItems as item (item.id)}
          {@render commandItem(item, false)}
        {/each}
      </Command.Group>
    {/each}
  </Command.List>
</Command.Dialog>
