<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import Plus from "@lucide/svelte/icons/plus";
  import Brackets from "@lucide/svelte/icons/brackets";
  import Droplet from "@lucide/svelte/icons/droplet";
  import TrendingUp from "@lucide/svelte/icons/trending-up";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Apple from "@lucide/svelte/icons/apple";
  import Clock from "@lucide/svelte/icons/clock";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Battery from "@lucide/svelte/icons/battery";
  import BatteryLow from "@lucide/svelte/icons/battery-low";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Fuel from "@lucide/svelte/icons/fuel";
  import RotateCcw from "@lucide/svelte/icons/rotate-ccw";
  import WifiOff from "@lucide/svelte/icons/wifi-off";
  import PauseCircle from "@lucide/svelte/icons/circle-pause";
  import Wand2 from "@lucide/svelte/icons/wand-sparkles";
  import ChartLine from "@lucide/svelte/icons/chart-line";
  import Activity from "@lucide/svelte/icons/activity";
  import Bell from "@lucide/svelte/icons/bell";
  import BellOff from "@lucide/svelte/icons/bell-off";
  import CalendarClock from "@lucide/svelte/icons/calendar-clock";
  import CalendarDays from "@lucide/svelte/icons/calendar-days";
  import Moon from "@lucide/svelte/icons/moon";
  import Timer from "@lucide/svelte/icons/timer";
  import {
    LEAF_FACTS,
    FACT_GROUP_ORDER,
    FACT_GROUP_LABELS,
    FACT_GROUP_COLOURS,
    type LeafKind,
    type LucideIconName,
  } from "./factCatalog";

  interface Props {
    onAddLeaf: (kind: LeafKind) => void;
    onAddGroup: (operator: "and" | "or") => void;
  }

  let { onAddLeaf, onAddGroup }: Props = $props();

  const ICONS: Record<LucideIconName, typeof Droplet> = {
    droplet: Droplet,
    "trending-up": TrendingUp,
    syringe: Syringe,
    apple: Apple,
    clock: Clock,
    "alert-triangle": AlertTriangle,
    battery: Battery,
    "battery-low": BatteryLow,
    smartphone: Smartphone,
    fuel: Fuel,
    "rotate-ccw": RotateCcw,
    "wifi-off": WifiOff,
    "pause-circle": PauseCircle,
    "wand-2": Wand2,
    "chart-line": ChartLine,
    activity: Activity,
    bell: Bell,
    "bell-off": BellOff,
    "calendar-clock": CalendarClock,
    "calendar-days": CalendarDays,
    moon: Moon,
    timer: Timer,
  };
</script>

<DropdownMenu.Root>
  <DropdownMenu.Trigger>
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        {...props}
        variant="dashed"
        size="sm"
        data-testid="alert-add-condition"
      >
        <Plus class="h-4 w-4 mr-2" /> Add condition
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
  <DropdownMenu.Content class="w-80 max-h-96" align="start" data-testid="alert-add-picker">
    {#each FACT_GROUP_ORDER as group (group)}
      {@const facts = LEAF_FACTS.filter((f) => f.group === group)}
      {#if facts.length > 0}
        <div class="px-2 pt-2 pb-1 text-2xs font-semibold uppercase tracking-wider text-muted-foreground">
          {FACT_GROUP_LABELS[group]}
        </div>
        {#each facts as f (f.kind)}
          {@const c = FACT_GROUP_COLOURS[f.group]}
          {@const Glyph = ICONS[f.icon]}
          <DropdownMenu.Item class="items-start" onSelect={() => onAddLeaf(f.kind)}>
            <span
              class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded {c.bg} {c.fg}"
              aria-hidden="true"
            >
              <Glyph class="h-3.5 w-3.5" />
            </span>
            <span class="flex flex-col">
              <span class="text-sm font-medium">{f.label}</span>
              <span class="text-xs text-muted-foreground leading-tight">{f.description}</span>
            </span>
          </DropdownMenu.Item>
        {/each}
      {/if}
    {/each}

    <DropdownMenu.Separator />
    <div class="px-2 pt-2 pb-1 text-2xs font-semibold uppercase tracking-wider text-muted-foreground">
      Group
    </div>
    <DropdownMenu.Item class="items-start" onSelect={() => onAddGroup("and")}>
      <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
        <Brackets class="h-3.5 w-3.5" />
      </span>
      <span class="flex flex-col">
        <span class="text-sm font-medium">+ Group (AND)</span>
        <span class="text-xs text-muted-foreground leading-tight">All sub-conditions must hold</span>
      </span>
    </DropdownMenu.Item>
    <DropdownMenu.Item class="items-start" onSelect={() => onAddGroup("or")}>
      <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
        <Brackets class="h-3.5 w-3.5" />
      </span>
      <span class="flex flex-col">
        <span class="text-sm font-medium">+ Group (OR)</span>
        <span class="text-xs text-muted-foreground leading-tight">Any sub-condition is enough</span>
      </span>
    </DropdownMenu.Item>
  </DropdownMenu.Content>
</DropdownMenu.Root>
