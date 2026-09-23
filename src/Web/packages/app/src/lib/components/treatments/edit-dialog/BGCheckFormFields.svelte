<script lang="ts">
  import type { GlucoseType, GlucoseUnit } from "$lib/api";
  import * as Select from "$lib/components/ui/select";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Droplet } from "lucide-svelte";

  interface Props {
    form: {
      glucose: number | null;
      glucoseType: GlucoseType | undefined;
      units: GlucoseUnit | undefined;
    };
  }

  let { form = $bindable() }: Props = $props();

  const glucoseTypeOptions: GlucoseType[] = ["Finger", "Sensor"] as GlucoseType[];
</script>

<div class="space-y-2">
  <Label for="glucose">
    <Droplet class="h-3.5 w-3.5 text-red-500" />
    Glucose
  </Label>
  <Input
    id="glucose"
    type="number"
    step="1"
    min="0"
    bind:value={form.glucose}
  />
</div>

<div class="grid grid-cols-2 gap-4">
  <div class="space-y-2">
    <Label>Glucose Type</Label>
    <Select.Root
      type="single"
      value={form.glucoseType ?? ""}
      onValueChange={(v) => {
        form.glucoseType = (v as GlucoseType) || undefined;
      }}
    >
      <Select.Trigger>
        {form.glucoseType || "Select..."}
      </Select.Trigger>
      <Select.Content>
        {#each glucoseTypeOptions as opt}
          <Select.Item value={opt}>{opt}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
  </div>
  <div class="space-y-2">
    <Label>Units</Label>
    <Select.Root
      type="single"
      value={form.units ?? ""}
      onValueChange={(v) => {
        form.units = (v as GlucoseUnit) || undefined;
      }}
    >
      <Select.Trigger>
        {form.units === "MgDl"
          ? "mg/dL"
          : form.units === "Mmol"
            ? "mmol/L"
            : "Select..."}
      </Select.Trigger>
      <Select.Content>
        <Select.Item value="MgDl">mg/dL</Select.Item>
        <Select.Item value="Mmol">mmol/L</Select.Item>
      </Select.Content>
    </Select.Root>
  </div>
</div>
