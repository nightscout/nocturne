<script lang="ts">
  import type { Bolus } from "$lib/api";
  import { BolusType } from "$lib/api";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import * as Select from "$lib/components/ui/select";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { Syringe, X, ChevronDown } from "lucide-svelte";

  interface Props {
    bolus: Partial<Bolus>;
    onRemove?: () => void;
  }

  let { bolus = $bindable(), onRemove }: Props = $props();

  let showAdvanced = $state(false);

  let showDuration = $derived(
    bolus.bolusType === BolusType.Square || bolus.bolusType === BolusType.Dual
  );

  let calculatedRate = $derived.by(() => {
    if (!bolus.insulin || !bolus.duration || bolus.duration <= 0) {
      return 0;
    }
    return bolus.insulin / (bolus.duration / 60);
  });

  const bolusTypeLabels: Record<BolusType, string> = {
    [BolusType.Normal]: "Normal",
    [BolusType.Square]: "Square Wave",
    [BolusType.Dual]: "Dual Wave",
  };
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Syringe class="h-4 w-4 text-blue-500" />
      Bolus
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="grid grid-cols-2 gap-3">
    <div class="space-y-1.5">
      <Label for="bolus-insulin">Insulin (U)</Label>
      <Input
        id="bolus-insulin"
        type="number"
        step="0.05"
        min="0"
        bind:value={bolus.insulin}
        placeholder="0.00"
      />
    </div>

    <div class="space-y-1.5">
      <Label for="bolus-type">Bolus Type</Label>
      <Select.Root
        type="single"
        value={bolus.bolusType ?? BolusType.Normal}
        onValueChange={(v) => {
          bolus.bolusType = v as BolusType;
        }}
      >
        <Select.Trigger id="bolus-type">
          {bolusTypeLabels[bolus.bolusType ?? BolusType.Normal]}
        </Select.Trigger>
        <Select.Content>
          {#each Object.values(BolusType) as bt}
            <Select.Item value={bt} label={bolusTypeLabels[bt]} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  </div>

  {#if showDuration}
    <div class="grid grid-cols-2 gap-3">
      <div class="space-y-1.5">
        <Label for="bolus-duration">Duration (min)</Label>
        <Input
          id="bolus-duration"
          type="number"
          step="1"
          min="0"
          bind:value={bolus.duration}
          placeholder="0"
        />
      </div>

      <div class="space-y-1.5">
        <Label for="bolus-rate">Rate (U/hr)</Label>
        <Input
          id="bolus-rate"
          type="text"
          readonly
          disabled
          value={calculatedRate > 0 ? calculatedRate.toFixed(3) : "-"}
          class="bg-muted text-muted-foreground"
        />
      </div>
    </div>
  {/if}

  <Collapsible.Root bind:open={showAdvanced}>
    <Collapsible.Trigger
      class="flex items-center gap-1 px-2 h-7 text-xs text-muted-foreground hover:text-foreground transition-colors"
    >
      <ChevronDown class="h-3 w-3 transition-transform {showAdvanced ? 'rotate-180' : ''}" />
      Advanced
    </Collapsible.Trigger>
    <Collapsible.Content>
      <div class="grid grid-cols-2 gap-3 pt-2">
        <div class="space-y-1.5">
          <Label for="bolus-programmed">Programmed (U)</Label>
          <Input
            id="bolus-programmed"
            type="number"
            step="0.05"
            min="0"
            bind:value={bolus.programmed}
            placeholder="0.00"
          />
        </div>

        <div class="space-y-1.5">
          <Label for="bolus-delivered">Delivered (U)</Label>
          <Input
            id="bolus-delivered"
            type="number"
            step="0.05"
            min="0"
            bind:value={bolus.delivered}
            placeholder="0.00"
          />
        </div>

        <div class="space-y-1.5">
          <Label for="bolus-insulin-type">Insulin Type</Label>
          <Input
            id="bolus-insulin-type"
            type="text"
            bind:value={bolus.insulinType}
            placeholder="e.g. Humalog"
          />
        </div>

        <div class="space-y-1.5">
          <Label for="bolus-unabsorbed">Unabsorbed (U)</Label>
          <Input
            id="bolus-unabsorbed"
            type="number"
            step="0.05"
            min="0"
            bind:value={bolus.unabsorbed}
            placeholder="0.00"
          />
        </div>
      </div>
    </Collapsible.Content>
  </Collapsible.Root>
</div>
