<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import type { InjectableMedication } from "$lib/api";
  import { Syringe } from "lucide-svelte";
  import { getActiveMedications } from "$lib/data/medications.remote";
  import { logDose } from "../../routes/settings/medications/data.remote";

  interface Props {
    open: boolean;
    onDoseLogged?: () => void;
  }

  let { open = $bindable(), onDoseLogged }: Props = $props();

  // Query active medications for the selector
  const medicationsQuery = $derived(getActiveMedications());

  // Form state
  let selectedMedicationId = $state("");
  let doseAmount = $state<number | undefined>(undefined);
  let timestamp = $state(formatDateTimeLocal(new Date()));
  let injectionSite = $state("");
  let notes = $state("");
  let isLoading = $state(false);

  // Category labels for grouping
  const categoryLabels: Record<string, string> = {
    RapidActing: "Rapid-Acting",
    UltraRapid: "Ultra-Rapid",
    ShortActing: "Short-Acting",
    Intermediate: "Intermediate",
    LongActing: "Long-Acting",
    UltraLong: "Ultra-Long",
    GLP1Daily: "GLP-1 Daily",
    GLP1Weekly: "GLP-1 Weekly",
    Other: "Other",
  };

  const injectionSiteOptions = [
    { value: "", label: "Not specified" },
    { value: "Abdomen", label: "Abdomen" },
    { value: "AbdomenLeft", label: "Abdomen (Left)" },
    { value: "AbdomenRight", label: "Abdomen (Right)" },
    { value: "ThighLeft", label: "Thigh (Left)" },
    { value: "ThighRight", label: "Thigh (Right)" },
    { value: "ArmLeft", label: "Arm (Left)" },
    { value: "ArmRight", label: "Arm (Right)" },
    { value: "Buttock", label: "Buttock" },
    { value: "Other", label: "Other" },
  ];

  function formatDateTimeLocal(date: Date): string {
    const pad = (n: number) => n.toString().padStart(2, "0");
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
  }

  function resetForm() {
    selectedMedicationId = "";
    doseAmount = undefined;
    timestamp = formatDateTimeLocal(new Date());
    injectionSite = "";
    notes = "";
  }

  // Group medications by category for the selector
  function groupMedications(
    medications: InjectableMedication[]
  ): { category: string; label: string; medications: InjectableMedication[] }[] {
    const groups: Record<string, InjectableMedication[]> = {};
    for (const med of medications) {
      const cat = med.category ?? "Other";
      if (!groups[cat]) groups[cat] = [];
      groups[cat].push(med);
    }
    return Object.entries(groups)
      .map(([category, meds]) => ({
        category,
        label: categoryLabels[category] ?? category,
        medications: meds.sort((a, b) =>
          (a.name ?? "").localeCompare(b.name ?? "")
        ),
      }))
      .sort(
        (a, b) =>
          Object.keys(categoryLabels).indexOf(a.category) -
          Object.keys(categoryLabels).indexOf(b.category)
      );
  }

  // Pre-fill dose amount when medication is selected
  let selectedMedication = $derived.by(() => {
    const meds = medicationsQuery.current;
    if (!meds || !selectedMedicationId) return null;
    return meds.find((m: InjectableMedication) => m.id === selectedMedicationId) ?? null;
  });

  function onMedicationChange(medId: string) {
    selectedMedicationId = medId;
    const meds = medicationsQuery.current;
    if (meds) {
      const med = meds.find((m: InjectableMedication) => m.id === medId);
      if (med?.defaultDose !== undefined && med?.defaultDose !== null) {
        doseAmount = med.defaultDose;
      }
    }
  }

  async function handleSubmit() {
    if (!selectedMedicationId || !doseAmount) return;
    isLoading = true;
    try {
      const timestampMills = new Date(timestamp).getTime();
      await logDose({
        injectableMedicationId: selectedMedicationId,
        units: doseAmount,
        timestamp: timestampMills,
        injectionSite: injectionSite || undefined,
        notes: notes.trim() || undefined,
      });
      resetForm();
      open = false;
      onDoseLogged?.();
    } catch (err) {
      console.error("Error logging dose:", err);
    } finally {
      isLoading = false;
    }
  }
</script>

<Dialog.Root bind:open onOpenChange={(isOpen) => { if (!isOpen) resetForm(); }}>
  <Dialog.Content class="sm:max-w-md">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        <Syringe class="h-5 w-5" />
        Log Injection
      </Dialog.Title>
      <Dialog.Description>
        Record a new injectable medication dose.
      </Dialog.Description>
    </Dialog.Header>

    {#await medicationsQuery}
      <div class="py-8 text-center text-muted-foreground">
        Loading medications...
      </div>
    {:then medications}
      {@const groups = groupMedications(medications)}
      <div class="space-y-4 py-4">
        <!-- Medication selector -->
        <div class="space-y-2">
          <Label>Medication</Label>
          <Select.Root
            type="single"
            value={selectedMedicationId}
            onValueChange={(v) => {
              if (v) onMedicationChange(v);
            }}
          >
            <Select.Trigger class="w-full">
              {selectedMedication?.name ?? "Select medication"}
            </Select.Trigger>
            <Select.Content>
              {#each groups as group}
                <Select.Group>
                  <Select.Label>{group.label}</Select.Label>
                  {#each group.medications as med}
                    <Select.Item value={med.id ?? ""}>
                      {med.name ?? "Unnamed"}
                    </Select.Item>
                  {/each}
                </Select.Group>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>

        <!-- Dose amount -->
        <div class="space-y-2">
          <Label for="dose-amount">
            Amount ({selectedMedication?.unitType === "Milligrams"
              ? "mg"
              : "units"})
          </Label>
          <Input
            id="dose-amount"
            type="number"
            step="0.5"
            min="0"
            placeholder={selectedMedication?.defaultDose?.toString() ?? "e.g. 10"}
            bind:value={doseAmount}
          />
        </div>

        <!-- Timestamp -->
        <div class="space-y-2">
          <Label for="dose-timestamp">Date & Time</Label>
          <Input
            id="dose-timestamp"
            type="datetime-local"
            bind:value={timestamp}
          />
        </div>

        <!-- Injection site -->
        <div class="space-y-2">
          <Label>Injection Site (optional)</Label>
          <Select.Root
            type="single"
            value={injectionSite}
            onValueChange={(v) => (injectionSite = v ?? "")}
          >
            <Select.Trigger class="w-full">
              {injectionSiteOptions.find((o) => o.value === injectionSite)
                ?.label ?? "Not specified"}
            </Select.Trigger>
            <Select.Content>
              {#each injectionSiteOptions as opt}
                <Select.Item value={opt.value}>{opt.label}</Select.Item>
              {/each}
            </Select.Content>
          </Select.Root>
        </div>

        <!-- Notes -->
        <div class="space-y-2">
          <Label for="dose-notes">Notes (optional)</Label>
          <Input
            id="dose-notes"
            placeholder="Any additional notes"
            bind:value={notes}
          />
        </div>
      </div>

      <Dialog.Footer>
        <Button
          variant="outline"
          onclick={() => {
            open = false;
            resetForm();
          }}
        >
          Cancel
        </Button>
        <Button
          onclick={handleSubmit}
          disabled={isLoading || !selectedMedicationId || !doseAmount}
        >
          {isLoading ? "Logging..." : "Log Dose"}
        </Button>
      </Dialog.Footer>
    {:catch}
      <div class="py-8 text-center text-muted-foreground">
        Failed to load medications. Please try again.
      </div>
    {/await}
  </Dialog.Content>
</Dialog.Root>
