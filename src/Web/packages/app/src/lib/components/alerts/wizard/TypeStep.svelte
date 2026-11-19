<script lang="ts">
  import { untrack, type Component } from "svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Collapsible from "#lib/components/ui/collapsible/index.js";
  import WizardOptionList from "#lib/components/wizard/WizardOptionList.svelte";
  import ArrowDown from "@lucide/svelte/icons/arrow-down";
  import ArrowUp from "@lucide/svelte/icons/arrow-up";
  import type { TrackerDefinitionDto } from "#lib/api/generated/nocturne-api-client.js";
  import BatteryLow from "@lucide/svelte/icons/battery-low";
  import CalendarClock from "@lucide/svelte/icons/calendar-clock";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
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
    /** The trackers there are; a type that reads only a tracker is listed when one matches. */
    trackerDefinitions?: readonly TrackerDefinitionDto[];
    /** Whether the presets are still on their way. */
    loading?: boolean;
  }

  let { selected, onPick, templates, trackerDefinitions = [], loading = false }: Props = $props();

  const option = (t: TemplateDef) => ({
    id: t.id,
    label: templateLabel(t.id),
    description: templateDescription(t.id),
  });

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

{#snippet icon(id: TemplateId)}
  {@const Icon = ICONS[id]}
  <Icon class="size-5" />
{/snippet}

<div class="space-y-3">
  <WizardOptionList
    label="Alert types"
    testIdPrefix="alert-type-"
    options={common.map(option)}
    {selected}
    {onPick}
    {icon}
    {loading}
  />

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
      <WizardOptionList
        label="More alert types"
        testIdPrefix="alert-type-"
        options={more.map(option)}
        {selected}
        {onPick}
        {icon}
        class="mt-1"
      />
    </Collapsible.Content>
  </Collapsible.Root>
</div>
