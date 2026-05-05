<script lang="ts">
  import type {
    CreateBasalInjectionRequest,
    PatientInsulin,
    InsulinCategory,
  } from "$lib/api";
  import * as Select from "$lib/components/ui/select";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Syringe, AlertTriangle } from "lucide-svelte";
  import { insulinCategoryLabels } from "$lib/components/patient/labels";

  interface Props {
    value: Partial<CreateBasalInjectionRequest>;
    basalInsulins?: PatientInsulin[];
  }

  let { value = $bindable(), basalInsulins = [] }: Props = $props();

  // Defensive fallback: if the parent hasn't pre-filtered to basal-eligible,
  // current insulins, do it here. The parent (Task 4.3) should be the one
  // applying timestamp-based active-date filtering.
  let eligibleInsulins = $derived(
    (basalInsulins ?? []).filter(
      (i) => i.isCurrent && (i.role === "Basal" || i.role === "Both")
    )
  );

  let selectedInsulin = $derived(
    eligibleInsulins.find((i) => i.id === value.patientInsulinId)
  );

  let isPremix = $derived(selectedInsulin?.role === "Both");

  let isHighDose = $derived(
    typeof value.units === "number" && value.units > 100
  );

  function handleInsulinSelect(next: string) {
    if (next === "") {
      value.patientInsulinId = undefined;
      return;
    }
    const insulin = eligibleInsulins.find((i) => i.id === next);
    if (insulin?.id) {
      value.patientInsulinId = insulin.id;
    }
  }
</script>

<div class="space-y-2">
  <Label class="flex items-center gap-1.5">
    <Syringe class="h-3.5 w-3.5 text-blue-500" />
    Basal Insulin
  </Label>
  <Select.Root
    type="single"
    value={value.patientInsulinId ?? ""}
    onValueChange={handleInsulinSelect}
  >
    <Select.Trigger>
      {selectedInsulin?.name ?? "Select insulin..."}
    </Select.Trigger>
    <Select.Content>
      {#each eligibleInsulins as insulin (insulin.id)}
        <Select.Item value={insulin.id ?? ""}>
          <div>
            <div>{insulin.name}</div>
            <div class="text-xs text-muted-foreground">
              {insulinCategoryLabels[insulin.insulinCategory as InsulinCategory] ?? insulin.insulinCategory}
            </div>
          </div>
        </Select.Item>
      {/each}
    </Select.Content>
  </Select.Root>
  {#if isPremix}
    <div class="flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-500">
      <AlertTriangle class="h-3.5 w-3.5 mt-0.5 shrink-0" />
      <span>
        This insulin is used for both bolus and basal &mdash; log the basal portion only.
      </span>
    </div>
  {/if}
</div>

<div class="space-y-2">
  <Label for="basal-units" class="flex items-center gap-1.5">
    <Syringe class="h-3.5 w-3.5 text-blue-500" />
    Units (U)
  </Label>
  <Input
    id="basal-units"
    type="number"
    step="0.5"
    min="0"
    max="500"
    bind:value={value.units}
  />
  {#if isHighDose}
    <div class="flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-500">
      <AlertTriangle class="h-3.5 w-3.5 mt-0.5 shrink-0" />
      <span>Confirm the dose &mdash; this is higher than typical.</span>
    </div>
  {/if}
</div>

<div class="space-y-2">
  <Label for="basal-notes">Notes</Label>
  <Textarea
    id="basal-notes"
    bind:value={value.notes}
    placeholder="Optional notes..."
    rows={3}
  />
</div>
