<script lang="ts">
  import type { BGCheck } from "$lib/api";
  import { GlucoseType, GlucoseUnit } from "$lib/api";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Button } from "$lib/components/ui/button";
  import * as Select from "$lib/components/ui/select";
  import { Droplet, X } from "lucide-svelte";

  interface Props {
    bgCheck: Partial<BGCheck>;
    onRemove?: () => void;
  }

  let { bgCheck = $bindable(), onRemove }: Props = $props();

  const glucoseTypeLabels: Record<GlucoseType, string> = {
    [GlucoseType.Finger]: "Finger",
    [GlucoseType.Sensor]: "Sensor",
  };

  const glucoseUnitLabels: Record<GlucoseUnit, string> = {
    [GlucoseUnit.MgDl]: "mg/dL",
    [GlucoseUnit.Mmol]: "mmol/L",
  };
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Droplet class="h-4 w-4 text-red-500" />
      BG Check
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="grid grid-cols-3 gap-3">
    <div class="space-y-1.5">
      <Label for="bg-glucose">Glucose</Label>
      <Input
        id="bg-glucose"
        type="number"
        step="1"
        min="0"
        bind:value={bgCheck.glucose}
        placeholder={bgCheck.units === GlucoseUnit.Mmol ? "0.0" : "0"}
      />
    </div>

    <div class="space-y-1.5">
      <Label for="bg-type">Type</Label>
      <Select.Root
        type="single"
        value={bgCheck.glucoseType ?? GlucoseType.Finger}
        onValueChange={(v) => {
          bgCheck.glucoseType = v as GlucoseType;
        }}
      >
        <Select.Trigger id="bg-type">
          {glucoseTypeLabels[bgCheck.glucoseType ?? GlucoseType.Finger]}
        </Select.Trigger>
        <Select.Content>
          {#each Object.values(GlucoseType) as gt}
            <Select.Item value={gt} label={glucoseTypeLabels[gt]} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>

    <div class="space-y-1.5">
      <Label for="bg-units">Units</Label>
      <Select.Root
        type="single"
        value={bgCheck.units ?? GlucoseUnit.MgDl}
        onValueChange={(v) => {
          bgCheck.units = v as GlucoseUnit;
        }}
      >
        <Select.Trigger id="bg-units">
          {glucoseUnitLabels[bgCheck.units ?? GlucoseUnit.MgDl]}
        </Select.Trigger>
        <Select.Content>
          {#each Object.values(GlucoseUnit) as gu}
            <Select.Item value={gu} label={glucoseUnitLabels[gu]} />
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  </div>
</div>
