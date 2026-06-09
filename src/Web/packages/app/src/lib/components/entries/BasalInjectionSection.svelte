<script lang="ts">
  import type { BasalInjection, PatientInsulin, InsulinCategory } from "$lib/api";
  import * as Select from "$lib/components/ui/select";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Button } from "$lib/components/ui/button";
  import { Syringe, X, AlertTriangle } from "lucide-svelte";
  import * as patientRemote from "$api/generated/patientRecords.generated.remote";
  import { insulinCategoryLabels } from "$lib/components/patient/labels";

  interface Props {
    injection: Partial<BasalInjection>;
    onRemove?: () => void;
  }

  let { injection = $bindable(), onRemove }: Props = $props();

  // Long-acting injections reference a configured PatientInsulin (Basal/Both
  // role). The hidden form submits insulinContext.patientInsulinId; the server
  // resolves the full TreatmentInsulinContext snapshot at write time.
  const insulinsResource = patientRemote.getInsulins();
  let patientInsulins = $derived(
    (insulinsResource.current ?? []) as PatientInsulin[],
  );

  let eligibleInsulins = $derived(
    patientInsulins.filter(
      (i) => i.isCurrent && (i.role === "Basal" || i.role === "Both"),
    ),
  );

  let selectedId = $derived(injection.insulinContext?.patientInsulinId);
  let selectedInsulin = $derived(
    eligibleInsulins.find((i) => i.id === selectedId),
  );
  let isPremix = $derived(selectedInsulin?.role === "Both");
  let isHighDose = $derived(
    typeof injection.units === "number" && injection.units > 100,
  );

  function handleInsulinSelect(next: string) {
    const insulin = eligibleInsulins.find((i) => i.id === next);
    if (!insulin?.id) {
      injection.insulinContext = undefined;
      return;
    }
    injection.insulinContext = {
      patientInsulinId: insulin.id,
      insulinName: insulin.name,
      dia: insulin.dia,
      peak: insulin.peak,
      curve: insulin.curve,
      concentration: insulin.concentration,
    };
  }
</script>

<div class="space-y-3">
  <div class="flex items-center justify-between">
    <div class="flex items-center gap-2 text-sm font-medium">
      <Syringe class="h-4 w-4 text-indigo-500" />
      Long-acting injection
    </div>
    {#if onRemove}
      <Button variant="ghost" size="icon" class="h-6 w-6" onclick={onRemove}>
        <X class="h-3.5 w-3.5" />
      </Button>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label>Basal Insulin</Label>
    <Select.Root
      type="single"
      value={selectedId ?? ""}
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
                {insulinCategoryLabels[
                  insulin.insulinCategory as InsulinCategory
                ] ?? insulin.insulinCategory}
              </div>
            </div>
          </Select.Item>
        {/each}
      </Select.Content>
    </Select.Root>
    {#if isPremix}
      <div
        class="flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-500"
      >
        <AlertTriangle class="h-3.5 w-3.5 mt-0.5 shrink-0" />
        <span>
          This insulin is used for both bolus and basal &mdash; log the basal
          portion only.
        </span>
      </div>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label for="basal-section-units">Units (U)</Label>
    <Input
      id="basal-section-units"
      type="number"
      step="0.5"
      min="0"
      max="500"
      bind:value={injection.units}
    />
    {#if isHighDose}
      <div
        class="flex items-start gap-1.5 text-xs text-amber-600 dark:text-amber-500"
      >
        <AlertTriangle class="h-3.5 w-3.5 mt-0.5 shrink-0" />
        <span>Confirm the dose &mdash; this is higher than typical.</span>
      </div>
    {/if}
  </div>

  <div class="space-y-1.5">
    <Label for="basal-section-notes">Notes</Label>
    <Textarea
      id="basal-section-notes"
      bind:value={injection.notes}
      placeholder="Optional notes..."
      rows={2}
    />
  </div>
</div>
