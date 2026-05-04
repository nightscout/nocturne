<script lang="ts">
  import type { BolusType, PatientInsulin, InsulinCategory } from "$lib/api";
  import * as Select from "$lib/components/ui/select";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Syringe } from "lucide-svelte";
  import { insulinCategoryLabels } from "$lib/components/patient/labels";

  interface Props {
    form: {
      insulin: number | null;
      bolusType: BolusType | undefined;
      programmed: number | undefined;
      delivered: number | undefined;
      duration: number | undefined;
      automatic: boolean;
      insulinType: string;
      patientInsulinId: string | undefined;
      isBasalInsulin: boolean;
    };
    patientInsulins?: PatientInsulin[];
    onAddInsulin?: () => void;
  }

  let { form = $bindable(), patientInsulins = [], onAddInsulin }: Props = $props();

  const bolusTypeOptions: BolusType[] = ["Normal", "Square", "Dual"] as BolusType[];

  const roleOrder: Record<string, number> = { Bolus: 0, Basal: 1, Both: 2 };
  let sortedInsulins = $derived(
    (patientInsulins ?? [])
      .filter((i) => i.isCurrent)
      .sort((a, b) => (roleOrder[a.role ?? ""] ?? 3) - (roleOrder[b.role ?? ""] ?? 3))
  );

  function handleInsulinSelect(value: string) {
    if (value === "__add_new__") {
      onAddInsulin?.();
      return;
    }
    if (value === "") {
      form.patientInsulinId = undefined;
      form.insulinType = "";
      return;
    }
    const insulin = sortedInsulins.find((i) => i.id === value);
    if (insulin) {
      form.patientInsulinId = insulin.id;
      form.insulinType = insulin.name ?? "";
      if (insulin.role === "Basal" || insulin.role === "Both") {
        form.isBasalInsulin = true;
      }
    }
  }
</script>

<div class="grid grid-cols-2 gap-4">
  <div class="space-y-2">
    <Label for="insulin" class="flex items-center gap-1.5">
      <Syringe class="h-3.5 w-3.5 text-blue-500" />
      Insulin (U)
    </Label>
    <Input
      id="insulin"
      type="number"
      step="0.05"
      min="0"
      bind:value={form.insulin}
    />
  </div>
  <div class="space-y-2">
    <Label>Bolus Type</Label>
    <Select.Root
      type="single"
      value={form.bolusType ?? ""}
      onValueChange={(v) => {
        form.bolusType = (v as BolusType) || undefined;
      }}
    >
      <Select.Trigger>
        {form.bolusType || "Select..."}
      </Select.Trigger>
      <Select.Content>
        {#each bolusTypeOptions as opt (opt)}
          <Select.Item value={opt}>{opt}</Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
  </div>
</div>

<div class="grid grid-cols-3 gap-4">
  <div class="space-y-2">
    <Label for="programmed">Programmed</Label>
    <Input
      id="programmed"
      type="number"
      step="0.05"
      min="0"
      bind:value={form.programmed}
      placeholder="\u2014"
    />
  </div>
  <div class="space-y-2">
    <Label for="delivered">Delivered</Label>
    <Input
      id="delivered"
      type="number"
      step="0.05"
      min="0"
      bind:value={form.delivered}
      placeholder="\u2014"
    />
  </div>
  <div class="space-y-2">
    <Label for="duration">Duration (min)</Label>
    <Input
      id="duration"
      type="number"
      step="1"
      min="0"
      bind:value={form.duration}
      placeholder="\u2014"
    />
  </div>
</div>

<div class="space-y-2">
  <Label>Insulin</Label>
  {#if form.insulinType && !form.patientInsulinId}
    <div class="mb-1.5">
      <Badge variant="secondary" class="text-xs">
        {form.insulinType} (unlinked)
      </Badge>
    </div>
  {/if}
  <Select.Root
    type="single"
    value={form.patientInsulinId ?? ""}
    onValueChange={handleInsulinSelect}
  >
    <Select.Trigger>
      {#if form.patientInsulinId}
        {sortedInsulins.find((i) => i.id === form.patientInsulinId)?.name ?? "Select insulin..."}
      {:else}
        Select insulin...
      {/if}
    </Select.Trigger>
    <Select.Content>
      {#each sortedInsulins as insulin (insulin.id)}
        <Select.Item value={insulin.id ?? ""}>
          <div>
            <div>{insulin.name}</div>
            <div class="text-xs text-muted-foreground">
              {insulinCategoryLabels[insulin.insulinCategory as InsulinCategory] ?? insulin.insulinCategory}
            </div>
          </div>
        </Select.Item>
      {/each}
      {#if sortedInsulins.length > 0}
        <div class="h-px bg-border my-1"></div>
      {/if}
      <Select.Item value="__add_new__">
        <div class="text-primary">Add new insulin...</div>
      </Select.Item>
    </Select.Content>
  </Select.Root>
</div>

<div class="flex gap-6">
  <div class="flex items-center gap-2">
    <Checkbox id="automatic" bind:checked={form.automatic} />
    <Label for="automatic" class="text-sm font-normal cursor-pointer">
      Automatic
    </Label>
  </div>
  <div class="flex items-center gap-2">
    <Checkbox
      id="isBasalInsulin"
      bind:checked={form.isBasalInsulin}
    />
    <Label
      for="isBasalInsulin"
      class="text-sm font-normal cursor-pointer"
    >
      Basal Insulin
    </Label>
  </div>
</div>
