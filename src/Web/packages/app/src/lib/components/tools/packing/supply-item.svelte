<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Label } from "$lib/components/ui/label";
  import type { SupplyItemConfig } from "./packing-config";

  interface Props {
    config: SupplyItemConfig;
    tripDays: number;
    avgTdd: number | null;
    hintInterval: number | null;
    enabled: boolean;
    quantity: number;
    onenablechange?: (enabled: boolean) => void;
  }

  let {
    config,
    tripDays,
    avgTdd,
    hintInterval,
    enabled = $bindable(config.defaultEnabled),
    quantity = $bindable(0),
    onenablechange,
  }: Props = $props();

  let interval = $state(config.defaultInterval ?? 0);
  let buffer = $state(config.defaultBuffer);
  let containerSize = $state(config.defaultContainerSize ?? 300);
  let flatQuantity = $state(1);

  $effect(() => {
    if (hintInterval != null && config.mode === "interval") {
      interval = Math.round(hintInterval * 10) / 10;
    }
  });

  const autoQuantity = $derived.by(() => {
    if (tripDays <= 0) return 0;
    if (config.mode === "interval") {
      if (interval <= 0) return 0;
      return Math.ceil(tripDays / interval) + buffer;
    }
    if (config.mode === "insulin") {
      if (!avgTdd || avgTdd <= 0 || containerSize <= 0) return 0;
      const totalInsulin = avgTdd * tripDays * (1 + buffer);
      return Math.ceil(totalInsulin / containerSize);
    }
    return flatQuantity;
  });

  // Sync quantity to parent
  $effect(() => {
    quantity = enabled ? autoQuantity : 0;
  });

  const insulinTotal = $derived.by(() => {
    if (config.mode !== "insulin" || !enabled) return null;
    return autoQuantity * containerSize;
  });

  function toggleEnabled(checked: boolean | "indeterminate") {
    enabled = checked === true;
    onenablechange?.(enabled);
  }
</script>

<div class="group rounded-lg transition-colors {enabled ? '' : 'opacity-40'}">
  <!-- Row 1: Checkbox + label + quantity -->
  <button
    type="button"
    class="flex w-full items-center gap-3 py-2.5 text-left"
    onclick={() => toggleEnabled(!enabled)}
  >
    <Checkbox checked={enabled} />
    <span class="flex-1 text-sm font-medium">{config.label}</span>
    {#if enabled && autoQuantity > 0}
      <span
        class="inline-flex items-center rounded-md bg-primary/10 px-2 py-0.5 text-sm font-semibold tabular-nums text-primary"
      >
        &times;{autoQuantity}
      </span>
    {/if}
  </button>

  <!-- Row 2: Config fields -->
  {#if enabled}
    <div class="ml-7 mb-2 rounded-md bg-muted/40 px-3 py-2.5 space-y-2">
      {#if config.mode === "interval"}
        <div class="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
          <div class="flex items-center gap-1.5">
            <Label class="text-muted-foreground text-xs">Change every</Label>
            <Input
              type="number"
              bind:value={interval}
              min={0.5}
              step={0.5}
              class="w-16 h-7 text-xs"
            />
            <span class="text-muted-foreground text-xs">days</span>
          </div>
          <div class="flex items-center gap-1.5">
            <Label class="text-muted-foreground text-xs">+</Label>
            <Input
              type="number"
              bind:value={buffer}
              min={0}
              step={1}
              class="w-14 h-7 text-xs"
            />
            <span class="text-muted-foreground text-xs">spare</span>
          </div>
        </div>
        {#if hintInterval != null}
          <p class="text-xs text-muted-foreground">
            Your average change interval is {hintInterval} days
          </p>
        {/if}

      {:else if config.mode === "insulin"}
        <div class="flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
          <div class="flex items-center gap-1.5">
            <Input
              type="number"
              bind:value={containerSize}
              min={1}
              step={10}
              class="w-20 h-7 text-xs"
            />
            <Label class="text-muted-foreground text-xs">u per container</Label>
          </div>
          <div class="flex items-center gap-1.5">
            <Input
              type="number"
              bind:value={buffer}
              min={0}
              max={2}
              step={0.1}
              class="w-16 h-7 text-xs"
            />
            <Label class="text-muted-foreground text-xs">
              ({Math.round(buffer * 100)}%) buffer
            </Label>
          </div>
        </div>
        <p class="text-xs text-muted-foreground">
          {#if avgTdd}
            ~{avgTdd}u/day average
          {/if}
          {#if insulinTotal}
            {#if avgTdd}&middot;{/if}
            {insulinTotal}u total
          {/if}
        </p>

      {:else}
        <div class="flex items-center gap-1.5 text-sm">
          <Label class="text-muted-foreground text-xs">Quantity</Label>
          <Input
            type="number"
            bind:value={flatQuantity}
            min={0}
            step={1}
            class="w-16 h-7 text-xs"
          />
        </div>
      {/if}
    </div>
  {/if}
</div>
