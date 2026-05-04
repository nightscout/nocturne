<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import { goto } from "$app/navigation";
  import {
    glucoseChartLookback,
    setColorScheme,
    getColorScheme,
    getGlucoseUnits,
    setGlucoseUnits,
  } from "$lib/stores/appearance-store.svelte";
  import { userPrefersMode } from "mode-watcher";
  import {
    items,
    groupMeta,
    type CommandPaletteItem,
    type CommandPaletteGroup,
  } from "./command-palette-items";
  import {
    pinnedItemIds,
    recentItemIds,
    togglePin,
    isPinned,
    recordRecent,
  } from "./command-palette-store.svelte";
  import CommandPaletteVitals from "./CommandPaletteVitals.svelte";
  import { Star } from "lucide-svelte";

  interface Props {
    open: boolean;
  }

  let { open = $bindable(false) }: Props = $props();

  let searchValue = $state("");

  const authStore = getAuthStore();
  const realtimeStore = getRealtimeStore();

  const visibleItems = $derived(
    items.filter(
      (item) => !item.permission || authStore.hasPermission(item.permission)
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
    const groups: Partial<Record<CommandPaletteGroup, CommandPaletteItem[]>> =
      {};
    for (const item of mainGroupItems) {
      (groups[item.group] ??= []).push(item);
    }

    const sorted = Object.entries(groups).sort(
      ([a], [b]) =>
        groupMeta[a as CommandPaletteGroup].order -
        groupMeta[b as CommandPaletteGroup].order
    ) as [CommandPaletteGroup, CommandPaletteItem[]][];

    return sorted;
  });

  function getStatValue(itemId: string): string | undefined {
    const pills = realtimeStore.pillsData;
    switch (itemId) {
      case "stat-iob":
        return pills.iob?.display ?? undefined;
      case "stat-cob":
        return pills.cob?.display ?? undefined;
      case "stat-cage":
        return pills.cage?.display ?? undefined;
      case "stat-sage":
        return pills.sage?.display ?? undefined;
      case "stat-a1c":
        return undefined;
      case "stat-tir":
        return undefined;
      default:
        return undefined;
    }
  }

  function getItemLabel(item: CommandPaletteItem): string {
    if (item.group === "quick-settings") {
      return getQuickSettingLabel(item.id) || item.label;
    }
    if (item.group === "stats") {
      const value = getStatValue(item.id);
      return value ? `${item.label}: ${value}` : item.label;
    }
    return item.label;
  }

  function getQuickSettingLabel(itemId: string): string {
    switch (itemId) {
      case "qs-dark-mode": {
        const mode = userPrefersMode.current;
        return `Dark Mode: ${mode === "dark" ? "On" : mode === "light" ? "Off" : "System"}`;
      }
      case "qs-glucose-units":
        return `Units: ${getGlucoseUnits() === "mg/dl" ? "mg/dL" : "mmol/L"}`;
      case "qs-chart-lookback":
        return `Chart Lookback: ${glucoseChartLookback.current}h`;
      default:
        return "";
    }
  }

  function handleSelect(item: CommandPaletteItem) {
    recordRecent(item.id);

    if (item.href) {
      open = false;
      goto(item.href);
    } else if (item.linkedHref) {
      open = false;
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
        goto("/reports/treatments");
        break;
      case "action-add-food":
        open = false;
        goto("/food");
        break;
      case "action-manual-sync":
        open = false;
        goto("/settings/connectors");
        break;
    }
  }

  function handlePinClick(e: MouseEvent, itemId: string) {
    e.stopPropagation();
    togglePin(itemId);
  }

  $effect(() => {
    if (!open) {
      searchValue = "";
    }
  });
</script>

{#snippet commandItem(item: CommandPaletteItem, pinAlwaysVisible: boolean)}
  {@const pinned = isPinned(item.id)}
  {#if item.href}
    <Command.LinkItem
      class="group/item"
      href={item.href}
      value={item.label}
      keywords={item.keywords}
      onSelect={() => handleSelect(item)}
    >
      {#if item.icon}
        {@const Icon = item.icon}
        <Icon class="mr-2 h-4 w-4" />
      {/if}
      <div class="flex flex-1 flex-col">
        <span>{getItemLabel(item)}</span>
        {#if item.description}
          <span class="text-xs text-muted-foreground"
            >{item.description}</span
          >
        {/if}
      </div>
      <button
        class="ml-auto shrink-0 p-1 {pinAlwaysVisible || pinned ? 'opacity-100' : 'opacity-0 group-hover/item:opacity-100'} transition-opacity"
        aria-label={pinned ? `Unpin ${item.label}` : `Pin ${item.label}`}
        onclick={(e) => handlePinClick(e, item.id)}
      >
        <Star
          class="h-3.5 w-3.5 {pinned ? 'fill-current text-yellow-500' : 'text-muted-foreground'}"
        />
      </button>
    </Command.LinkItem>
  {:else}
    <Command.Item
      class="group/item"
      value={item.label}
      keywords={item.keywords}
      onSelect={() => handleSelect(item)}
    >
      {#if item.icon}
        {@const Icon = item.icon}
        <Icon class="mr-2 h-4 w-4" />
      {/if}
      <div class="flex flex-1 flex-col">
        <span>{getItemLabel(item)}</span>
        {#if item.description}
          <span class="text-xs text-muted-foreground"
            >{item.description}</span
          >
        {/if}
      </div>
      <button
        class="ml-auto shrink-0 p-1 {pinAlwaysVisible || pinned ? 'opacity-100' : 'opacity-0 group-hover/item:opacity-100'} transition-opacity"
        aria-label={pinned ? `Unpin ${item.label}` : `Pin ${item.label}`}
        onclick={(e) => handlePinClick(e, item.id)}
      >
        <Star
          class="h-3.5 w-3.5 {pinned ? 'fill-current text-yellow-500' : 'text-muted-foreground'}"
        />
      </button>
    </Command.Item>
  {/if}
{/snippet}

<Command.Dialog bind:open>
  <Command.Input placeholder="Search commands..." bind:value={searchValue} />

  <CommandPaletteVitals />

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
      <Command.Group heading={groupMeta[group].label}>
        {#each groupItems as item (item.id)}
          {@render commandItem(item, false)}
        {/each}
      </Command.Group>
    {/each}
  </Command.List>
</Command.Dialog>
