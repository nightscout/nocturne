<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
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
  };

  const PICKER_BTN =
    "flex h-auto w-full items-start justify-start gap-2 rounded px-2 py-1.5 text-left font-normal hover:bg-muted";
</script>

<Popover.Root>
  <Popover.Trigger>
    {#snippet child({ props })}
      <Button
        {...props}
        variant="outline"
        size="sm"
        class="border-dashed text-muted-foreground"
      >
        <Plus class="h-4 w-4 mr-2" /> Add condition
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-80 p-1" align="start">
    <div class="max-h-96 overflow-y-auto">
      {#each FACT_GROUP_ORDER as group (group)}
        {@const facts = LEAF_FACTS.filter((f) => f.group === group)}
        {#if facts.length > 0}
          <div class="px-2 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
            {FACT_GROUP_LABELS[group]}
          </div>
          {#each facts as f (f.kind)}
            {@const c = FACT_GROUP_COLOURS[f.group]}
            {@const Glyph = ICONS[f.icon]}
            <Popover.Close>
              {#snippet child({ props })}
                <Button
                  {...props}
                  type="button"
                  variant="ghost"
                  class={PICKER_BTN}
                  onclick={() => onAddLeaf(f.kind)}
                >
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
                </Button>
              {/snippet}
            </Popover.Close>
          {/each}
        {/if}
      {/each}

      <div class="my-1 border-t"></div>
      <div class="px-2 pt-2 pb-1 text-[10px] font-semibold uppercase tracking-wider text-muted-foreground">
        Group
      </div>
      <Popover.Close>
        {#snippet child({ props })}
          <Button
            {...props}
            type="button"
            variant="ghost"
            class={PICKER_BTN}
            onclick={() => onAddGroup("and")}
          >
            <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
              <Brackets class="h-3.5 w-3.5" />
            </span>
            <span class="flex flex-col">
              <span class="text-sm font-medium">+ Group (AND)</span>
              <span class="text-xs text-muted-foreground leading-tight">All sub-conditions must hold</span>
            </span>
          </Button>
        {/snippet}
      </Popover.Close>
      <Popover.Close>
        {#snippet child({ props })}
          <Button
            {...props}
            type="button"
            variant="ghost"
            class={PICKER_BTN}
            onclick={() => onAddGroup("or")}
          >
            <span class="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded bg-muted text-muted-foreground">
              <Brackets class="h-3.5 w-3.5" />
            </span>
            <span class="flex flex-col">
              <span class="text-sm font-medium">+ Group (OR)</span>
              <span class="text-xs text-muted-foreground leading-tight">Any sub-condition is enough</span>
            </span>
          </Button>
        {/snippet}
      </Popover.Close>
    </div>
  </Popover.Content>
</Popover.Root>
