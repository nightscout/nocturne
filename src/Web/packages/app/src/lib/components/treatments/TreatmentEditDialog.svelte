<script lang="ts">
  import {
    type Treatment,
    type InjectableMedication,
    InjectableCategory,
  } from "$lib/api";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Badge } from "$lib/components/ui/badge";
  import EventTypeCombobox from "./EventTypeCombobox.svelte";
  import TreatmentFoodBreakdown from "./TreatmentFoodBreakdown.svelte";
  import * as Select from "$lib/components/ui/select";
  import {
    Syringe,
    Apple,
    Clock,
    User,
    MessageSquare,
    Database,
    Braces,
    Activity,
    Trash2,
  } from "lucide-svelte";
  import { getEventTypeStyle } from "$lib/constants/treatment-categories";
  import { formatDateTime, formatDateForInput } from "$lib/utils/formatting";
  import { getActiveMedications } from "$lib/data/medications.remote";

  interface Props {
    open: boolean;
    treatment: Treatment | null;
    availableEventTypes?: string[];
    isLoading?: boolean;
    onClose: () => void;
    onSave: (treatment: Treatment) => void;
    onDelete?: (treatmentId: string) => void;
  }

  let {
    open = $bindable(),
    treatment,
    availableEventTypes = [],
    isLoading = false,
    onClose,
    onSave,
    onDelete,
  }: Props = $props();

  // Query active medications for insulin selector
  const medicationsQuery = $derived(getActiveMedications());

  // Editable form state
  let formState = $state<{
    created_at: string;
    eventType: string;
    insulin: number | undefined;
    carbs: number | undefined;
    glucose: number | undefined;
    duration: number | undefined;
    notes: string;
    profile: string;
    additionalPropertiesJson: string;
    injectableMedicationId: string;
  }>({
    created_at: new Date().toISOString(),
    eventType: "",
    insulin: undefined,
    carbs: undefined,
    glucose: undefined,
    duration: undefined,
    notes: "",
    profile: "",
    additionalPropertiesJson: "",
    injectableMedicationId: "",
  });

  // Reset form when treatment changes
  $effect(() => {
    if (treatment) {
      formState = {
        created_at: treatment.created_at || new Date().toISOString(),
        eventType: treatment.eventType || "",
        insulin: treatment.insulin ?? undefined,
        carbs: treatment.carbs ?? undefined,
        glucose: treatment.glucose ?? undefined,
        duration: treatment.duration ?? undefined,
        notes: treatment.notes || "",
        profile: treatment.profile || "",
        additionalPropertiesJson: treatment.additional_properties
          ? JSON.stringify(treatment.additional_properties, null, 2)
          : "",
        injectableMedicationId: (treatment as any).injectableMedicationId || "",
      };
    } else {
      // Reset to defaults for creation
      formState = {
        created_at: new Date().toISOString(),
        eventType:
          availableEventTypes.length === 1 ? availableEventTypes[0] : "",
        insulin: undefined,
        carbs: undefined,
        glucose: undefined,
        duration: undefined,
        notes: "",
        profile: "",
        additionalPropertiesJson: "",
        injectableMedicationId: "",
      };
    }
  });

  // Check if additional_properties JSON is valid
  let additionalPropertiesError = $derived.by(() => {
    if (!formState.additionalPropertiesJson.trim()) return null;
    try {
      JSON.parse(formState.additionalPropertiesJson);
      return null;
    } catch (e) {
      return "Invalid JSON";
    }
  });

  function handleSubmit() {
    if (additionalPropertiesError) return;

    let additionalProps: Record<string, unknown> | undefined = undefined;
    if (formState.additionalPropertiesJson.trim()) {
      try {
        additionalProps = JSON.parse(formState.additionalPropertiesJson);
      } catch {
        // Already validated above
      }
    }

    const updated: Treatment = {
      ...treatment,
      created_at: formState.created_at,
      eventType: formState.eventType || undefined,
      insulin: formState.insulin || undefined,
      carbs: formState.carbs || undefined,
      glucose: formState.glucose || undefined,
      duration: formState.duration || undefined,
      notes: formState.notes || undefined,
      profile: formState.profile || undefined,
      additional_properties: additionalProps,
    };
    // Add injectable medication ID if selected (NocturneOnly field)
    if (formState.injectableMedicationId) {
      (updated as any).injectableMedicationId =
        formState.injectableMedicationId;
    }

    onSave(updated);
  }

  let style = $derived(getEventTypeStyle(formState.eventType));
  let hasAdditionalProperties = $derived(
    treatment?.additional_properties &&
      Object.keys(treatment.additional_properties).length > 0
  );
  let calculatedRate = $derived.by(() => {
    if (!formState.insulin || !formState.duration || formState.duration <= 0) {
      return 0;
    }
    return formState.insulin / (formState.duration / 60);
  });

  // We track resolved medications separately since medicationsQuery is a promise
  let resolvedMedications = $state<InjectableMedication[]>([]);
  $effect(() => {
    medicationsQuery.then((meds) => {
      resolvedMedications = meds;
    });
  });

  let isGlp = $derived.by(() => {
    if (!formState.injectableMedicationId || resolvedMedications.length === 0)
      return false;
    const med = resolvedMedications.find(
      (m) => m.id === formState.injectableMedicationId
    );
    return (
      med?.category === InjectableCategory.GLP1Daily ||
      med?.category === InjectableCategory.GLP1Weekly
    );
  });

  let doseLabel = $derived(isGlp ? "Dose (mg)" : "Insulin (U)");
  let doseStep = $derived(isGlp ? "0.25" : "0.05");
</script>

<Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        {treatment?._id ? "Edit Treatment" : "New Treatment"}
        {#if formState.eventType}
          <Badge variant="outline" class={style.colorClass}>
            {formState.eventType}
          </Badge>
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        {treatment?._id
          ? "Edit the details of this treatment entry."
          : "Enter details for the new treatment."}
        Changes will be saved to the database.
      </Dialog.Description>
    </Dialog.Header>

    {#if true}
      <form
        onsubmit={(e) => {
          e.preventDefault();
          handleSubmit();
        }}
        class="space-y-4"
      >
        <!-- Read-only metadata -->
        {#if treatment?._id}
          <div
            class="flex flex-wrap gap-4 text-sm text-muted-foreground bg-muted/30 rounded-lg p-3"
          >
            <div class="flex items-center gap-1.5">
              <Clock class="h-3.5 w-3.5" />
              <span>{formatDateTime(treatment.created_at)}</span>
            </div>
            {#if treatment.enteredBy}
              <div class="flex items-center gap-1.5">
                <User class="h-3.5 w-3.5" />
                <span>{treatment.enteredBy}</span>
              </div>
            {/if}
            {#if treatment.data_source}
              <div class="flex items-center gap-1.5">
                <Database class="h-3.5 w-3.5" />
                <span>{treatment.data_source}</span>
              </div>
            {/if}
          </div>
        {/if}

        <!-- Event Type Combobox -->
        <div class="space-y-2">
          <Label>Event Type</Label>
          <EventTypeCombobox
            bind:value={formState.eventType}
            onSelect={(type) => (formState.eventType = type)}
            additionalEventTypes={availableEventTypes}
            placeholder="Select or enter event type..."
          />
        </div>

        <!-- Date and Time -->
        <div>
          <Label for="datetime">Date & Time</Label>
          <Input
            id="datetime"
            type="datetime-local"
            value={formatDateForInput(formState.created_at)}
            onchange={(e) => {
              const val = e.currentTarget.value;
              if (val) formState.created_at = new Date(val).toISOString();
            }}
          />
        </div>

        <!-- Medication Type Selector -->
        {#await medicationsQuery then medications}
          {#if medications.length > 0}
            <div class="space-y-2">
              <Label class="flex items-center gap-1.5">
                <Syringe class="h-3.5 w-3.5" />
                Medication Type
              </Label>
              <Select.Root
                type="single"
                value={formState.injectableMedicationId}
                onValueChange={(v) =>
                  (formState.injectableMedicationId = v ?? "")}
              >
                <Select.Trigger class="w-full">
                  {(() => {
                    const med = formState.injectableMedicationId
                      ? medications.find(
                          (m) => m.id === formState.injectableMedicationId
                        )
                      : null;
                    return med ? med.name : "Default (from profile)";
                  })()}
                </Select.Trigger>
                <Select.Content>
                  <Select.Item value="">Default (from profile)</Select.Item>
                  {#each medications as med}
                    <Select.Item value={med.id ?? ""}>
                      {med.name}
                      {#if med.dia}
                        <span class="text-muted-foreground text-xs ml-1">
                          ({med.dia}h DIA)
                        </span>
                      {/if}
                    </Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
          {/if}
        {/await}

        <!-- Insulin/Dose, Duration & Rate Row -->
        <div class="grid {isGlp ? 'grid-cols-1' : 'grid-cols-3'} gap-4">
          <div class="space-y-2">
            <Label for="insulin" class="flex items-center gap-1.5">
              <Syringe class="h-3.5 w-3.5 text-blue-500" />
              {doseLabel}
            </Label>
            <Input
              id="insulin"
              type="number"
              step={doseStep}
              min="0"
              bind:value={formState.insulin}
              placeholder="0.0"
            />
          </div>

          {#if !isGlp}
            <div class="space-y-2">
              <Label for="duration">Duration (min)</Label>
              <Input
                id="duration"
                type="number"
                step="1"
                min="0"
                bind:value={formState.duration}
                placeholder="0"
              />
            </div>

            <div class="space-y-2">
              <Label for="rate" class="flex items-center gap-1.5">
                <Activity class="h-3.5 w-3.5" />
                Rate (U/hr)
              </Label>
              <Input
                id="rate"
                type="text"
                readonly
                disabled
                value={calculatedRate > 0 ? calculatedRate.toFixed(3) : "-"}
                class="bg-muted text-muted-foreground"
              />
            </div>
          {/if}
        </div>

        <!-- Carbs & Glucose Row -->
        <div class="grid grid-cols-2 gap-4">
          <div class="space-y-2">
            <Label for="carbs" class="flex items-center gap-1.5">
              <Apple class="h-3.5 w-3.5 text-green-500" />
              Carbs (g)
            </Label>
            <Input
              id="carbs"
              type="number"
              step="1"
              min="0"
              bind:value={formState.carbs}
              placeholder="0"
            />
          </div>

          <div class="space-y-2">
            <Label for="glucose">Blood Glucose</Label>
            <Input
              id="glucose"
              type="number"
              step="1"
              min="0"
              bind:value={formState.glucose}
              placeholder="mg/dL"
            />
          </div>
        </div>

        {#if treatment?._id}
          <TreatmentFoodBreakdown
            treatmentId={treatment._id}
            totalCarbs={formState.carbs ?? 0}
          />
        {/if}

        <!-- Profile -->
        <div class="space-y-2">
          <Label for="profile">Profile</Label>
          <Input
            id="profile"
            bind:value={formState.profile}
            placeholder="Profile name"
          />
        </div>

        <!-- Notes -->
        <div class="space-y-2">
          <Label for="notes" class="flex items-center gap-1.5">
            <MessageSquare class="h-3.5 w-3.5" />
            Notes
          </Label>
          <Textarea
            id="notes"
            bind:value={formState.notes}
            placeholder="Add notes..."
            rows={3}
          />
        </div>

        <!-- Additional Properties -->
        {#if hasAdditionalProperties || formState.additionalPropertiesJson.trim()}
          <div class="space-y-2">
            <Label for="additionalProperties" class="flex items-center gap-1.5">
              <Braces class="h-3.5 w-3.5" />
              Additional Properties
              {#if additionalPropertiesError}
                <Badge variant="destructive" class="text-xs">
                  {additionalPropertiesError}
                </Badge>
              {/if}
            </Label>
            <Textarea
              id="additionalProperties"
              bind:value={formState.additionalPropertiesJson}
              placeholder="&#123;&#125;"
              rows={5}
              class="font-mono text-xs {additionalPropertiesError
                ? 'border-destructive'
                : ''}"
            />
          </div>
        {/if}

        <Dialog.Footer class="gap-2">
          {#if treatment?._id && onDelete}
            <Button
              type="button"
              variant="destructive"
              onclick={() => treatment?._id && onDelete(treatment._id)}
              disabled={isLoading}
              class="mr-auto"
            >
              <Trash2 class="mr-2 h-4 w-4" />
              Delete
            </Button>
          {/if}
          <Button
            type="button"
            variant="outline"
            onclick={onClose}
            disabled={isLoading}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={isLoading}>
            {isLoading ? "Saving..." : "Save Changes"}
          </Button>
        </Dialog.Footer>
      </form>
    {/if}
  </Dialog.Content>
</Dialog.Root>
