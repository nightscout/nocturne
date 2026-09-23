<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import {
    Plus,
    Brackets,
    Droplet,
    TrendingUp,
    Syringe,
    Apple,
    Clock,
    AlertTriangle,
    Battery,
    BatteryLow,
    Smartphone,
    Fuel,
    RotateCcw,
    WifiOff,
    PauseCircle,
    Wand2,
    ChartLine,
    Activity,
    Bell,
    BellOff,
    CalendarClock,
    CalendarDays,
    Moon,
    Timer,
  } from "lucide-svelte";
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
      >
        <Plus class="h-4 w-4 mr-2" /> Add condition
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
  <DropdownMenu.Content class="w-80 max-h-96" align="start">
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
