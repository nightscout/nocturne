<script lang="ts">
  import { untrack, type Component } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Skeleton } from "#lib/components/ui/skeleton/index.js";
  import { DropGroup, DropSurface } from "water-ui";
  import { itemVariants } from "#lib/components/ui/item/index.js";
  import { cn } from "#lib/utils/index.js";
  import { stillArtwork } from "#lib/watercolour-deferral.svelte.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import type { TrackerDefinitionDto } from "#lib/api/generated/nocturne-api-client.js";
  import BatteryLow from "@lucide/svelte/icons/battery-low";
  import CalendarClock from "@lucide/svelte/icons/calendar-clock";
  import Check from "@lucide/svelte/icons/check";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import ChevronRight from "@lucide/svelte/icons/chevron-right";
  import ChevronsDown from "@lucide/svelte/icons/chevrons-down";
  import Fuel from "@lucide/svelte/icons/fuel";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import SlidersHorizontal from "@lucide/svelte/icons/sliders-horizontal";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import TrendingDown from "@lucide/svelte/icons/trending-down";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import WifiOff from "@lucide/svelte/icons/wifi-off";
  import {
    offeredTemplates,
    templateDescription,
    templateLabel,
    type TemplateDef,
    type TemplateId,
  } from "./templates.svelte";

  interface Props {
    selected: TemplateId | null;
    onPick: (id: TemplateId) => void;
    /** The types there are, from the API's presets. */
    templates: readonly TemplateDef[];
    /** The presets have not arrived yet. */
    loading?: boolean;
    /** The trackers there are; a type that reads only a tracker is listed when one matches. */
    trackerDefinitions?: readonly TrackerDefinitionDto[];
  }

  let { selected, onPick, templates, loading = false, trackerDefinitions = [] }: Props = $props();

  // The padding is on the content, which sits above the paint.
  const ROW = cn(itemVariants({ variant: "row", interactive: true }), "p-0");

  // Absolutely positioned over the row, so the list lays out the same before it arrives.
  const selectionEdge = stillArtwork(() => import("#lib/watercolour/selection-edge.js"));

  const ICONS: Record<TemplateId, Component> = {
    urgent_low: ChevronsDown,
    low: ArrowDown,
    high: ArrowUp,
    falling_fast: TrendingDown,
    rising_fast: TrendingUp,
    no_readings: WifiOff,
    urgent_low_soon: ChevronsDown,
    reservoir_low: Fuel,
    pump_battery_low: BatteryLow,
    battery_change_due: CalendarClock,
    site_change_due: CalendarClock,
    sensor_change_due: CalendarClock,
    loop_not_running: RotateCcw,
    phone_battery_low: Smartphone,
    custom: SlidersHorizontal,
  };

  const offered = $derived(offeredTemplates(templates, trackerDefinitions));
  const common = $derived(offered.filter((t) => t.group === "common"));
  const more = $derived(offered.filter((t) => t.group === "more"));

  // A choice made earlier from the longer list stays in view on the way back.
  let moreOpen = $state(untrack(() => more.some((t) => t.id === selected)));
</script>

{#snippet row(id: TemplateId)}
  {@const Icon = ICONS[id]}
  {@const isSelected = selected === id}
  <li class="relative">
    <!-- The row is the surface, so the paint lands under its copy; the chosen row has the edge
         and stays plain. -->
    <DropSurface
      as="button"
      type="button"
      name={id}
      shown={isSelected ? false : undefined}
      data-testid="alert-type-{id}"
      aria-current={isSelected ? "true" : undefined}
      onclick={() => onPick(id)}
      class={ROW}
      contentClass="flex w-full items-center gap-3 p-3"
    >
      <Icon data-drop-obstacle class="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span class="min-w-0 flex-1">
        <span data-drop-obstacle class="block font-medium">{templateLabel(id)}</span>
        <span data-drop-obstacle class="block text-sm text-muted-foreground">
          {templateDescription(id)}
        </span>
      </span>
      {#if isSelected}
        <Check class="size-4 shrink-0 text-primary" aria-hidden="true" />
      {:else}
        <ChevronRight class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      {/if}
    </DropSurface>
    {#if selectionEdge.component}
      <selectionEdge.component active={isSelected} />
    {/if}
  </li>
{/snippet}

<div class="space-y-3">
  <DropGroup name="alert types">
    <ul class="divide-y border-y" aria-label="Alert types" aria-busy={loading}>
      {#if loading}
        {#each { length: 6 }, index (index)}
          <li class="p-3"><Skeleton class="h-11 w-full" /></li>
        {/each}
      {/if}
      {#each common as template (template.id)}
        {@render row(template.id)}
      {/each}
    </ul>
  </DropGroup>

  <Collapsible.Root bind:open={moreOpen}>
    <Collapsible.Trigger>
      {#snippet child({ props }: { props: Record<string, unknown> })}
        <Button
          {...props}
          variant="subtle"
          size="touch-sm"
          class="group"
          data-testid="alert-type-more"
        >
          <ChevronDown
            class="transition-transform duration-200 group-data-[state=open]:rotate-180"
            aria-hidden="true"
          />
          More alert types
        </Button>
      {/snippet}
    </Collapsible.Trigger>
    <Collapsible.Content>
      <DropGroup name="more alert types">
        <ul class="mt-1 divide-y border-y" aria-label="More alert types">
          {#each more as template (template.id)}
            {@render row(template.id)}
          {/each}
        </ul>
      </DropGroup>
    </Collapsible.Content>
  </Collapsible.Root>
</div>
