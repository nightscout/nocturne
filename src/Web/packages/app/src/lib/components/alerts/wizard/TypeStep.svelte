<script lang="ts">
  import type { Component } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import { Item } from "#lib/components/ui/item/index.js";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
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
    TEMPLATES,
    templateDescription,
    templateLabel,
    type TemplateId,
  } from "./templates.svelte";

  interface Props {
    selected: TemplateId | null;
    onPick: (id: TemplateId) => void;
  }

  let { selected, onPick }: Props = $props();

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
    site_change_due: CalendarClock,
    sensor_change_due: CalendarClock,
    loop_not_running: RotateCcw,
    phone_battery_low: Smartphone,
    custom: SlidersHorizontal,
  };

  const common = TEMPLATES.filter((t) => t.group === "common");
  const more = TEMPLATES.filter((t) => t.group === "more");

  // A choice made earlier from the longer list stays in view on the way back.
  let moreOpen = $state(more.some((t) => t.id === selected));
</script>

{#snippet row(id: TemplateId)}
  {@const Icon = ICONS[id]}
  {@const isSelected = selected === id}
  <li>
    <Item
      variant="row"
      data-testid="alert-type-{id}"
      aria-current={isSelected ? "true" : undefined}
      onclick={() => onPick(id)}
    >
      <Icon class="size-5 shrink-0 text-muted-foreground" aria-hidden="true" />
      <span class="min-w-0 flex-1">
        <span class="block font-medium">{templateLabel(id)}</span>
        <span class="block text-sm text-muted-foreground">{templateDescription(id)}</span>
      </span>
      {#if isSelected}
        <Check class="size-4 shrink-0 text-primary" aria-hidden="true" />
      {:else}
        <ChevronRight class="size-4 shrink-0 text-muted-foreground" aria-hidden="true" />
      {/if}
    </Item>
  </li>
{/snippet}

<div class="space-y-3">
  <ul class="divide-y border-y" aria-label="Alert types">
    {#each common as template (template.id)}
      {@render row(template.id)}
    {/each}
  </ul>

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
      <ul class="mt-1 divide-y border-y" aria-label="More alert types">
        {#each more as template (template.id)}
          {@render row(template.id)}
        {/each}
      </ul>
    </Collapsible.Content>
  </Collapsible.Root>
</div>
