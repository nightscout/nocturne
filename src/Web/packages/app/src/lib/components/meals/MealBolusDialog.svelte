<script lang="ts">
  import {
    formatClock,
    formatInsulinDisplay,
    formatLocale,
  } from "#lib/utils/formatting.js";
  import type { MealEvent, Bolus, BolusType, PatientInsulin } from "#lib/api/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import BolusFormFields from "#lib/components/treatments/edit-dialog/BolusFormFields.svelte";
  import * as patientRemote from "#lib/api/generated/patientRecords.generated.remote.js";
  import {
    create as createBolusForm,
    update as updateBolusForm,
    remove as removeBolus,
  } from "#lib/api/generated/bolus.generated.remote.js";
  import { toast } from "svelte-sonner";
  import { tryGetRealtimeStore } from "#lib/stores/realtime-store.svelte.js";
  import { useSubmission, useToastSubmission } from "#lib/forms/index.js";
  import Syringe from "@lucide/svelte/icons/syringe";
  import Plus from "@lucide/svelte/icons/plus";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";

  interface Props {
    open: boolean;
    onOpenChange: (open: boolean) => void;
    meal: MealEvent | null;
    onSave: () => void;
  }

  let { open = $bindable(), onOpenChange, meal, onSave }: Props = $props();

  let mode = $state<"list" | "edit">("list");
  let editingBolus = $state<Bolus | null>(null);
  let isSaving = $state(false);
  let deletingBolusId = $state<string | null>(null);
  const deletion = useToastSubmission("Failed to delete bolus");
  const saving = useSubmission({ fallback: "Failed to save bolus" });

  const realtimeStore = tryGetRealtimeStore();

  interface BolusEditForm {
    insulin: number | null;
    bolusType: BolusType | undefined;
    programmed: number | undefined;
    delivered: number | undefined;
    duration: number | undefined;
    automatic: boolean;
    insulinType: string;
    patientInsulinId: string | undefined;
    isBasalInsulin: boolean;
  }

  let editForm = $state<BolusEditForm>({
    insulin: null,
    bolusType: undefined,
    programmed: undefined,
    delivered: undefined,
    duration: undefined,
    automatic: false,
    insulinType: "",
    patientInsulinId: undefined,
    isBasalInsulin: false,
  });

  // Form element refs for programmatic submission
  let formRef = $state<HTMLFormElement | null>(null);

  // Determine which form action to use
  const activeForm = $derived(
    editingBolus ? updateBolusForm : createBolusForm,
  );

  // Fetch patient insulins for the form dropdown
  const insulinsResource = patientRemote.getInsulins();
  let patientInsulins: PatientInsulin[] = $derived(insulinsResource.current ?? []);

  // Reset to list mode when dialog opens
  $effect(() => {
    if (open) {
      mode = "list";
      editingBolus = null;
      deletingBolusId = null;
    }
  });

  // The locale's own medium date with a short time. Not the shared
  // `formatMediumDateTime`, whose explicit field set spells the month out where
  // this shape stays numeric — de-DE, ja-JP, ko-KR and ar-EG all differ.
  let mealTimestamp = $derived(
    meal?.timestamp
      ? new Date(meal.timestamp).toLocaleString(formatLocale(), {
          dateStyle: "medium",
          timeStyle: "short",
        })
      : "",
  );

  function startEdit(bolus: Bolus | null) {
    editingBolus = bolus;
    if (bolus) {
      editForm = {
        insulin: bolus.insulin ?? null,
        bolusType: bolus.bolusType ?? undefined,
        programmed: bolus.programmed ?? undefined,
        delivered: bolus.delivered ?? undefined,
        duration: bolus.duration ?? undefined,
        automatic: bolus.automatic ?? false,
        insulinType: bolus.insulinType ?? "",
        patientInsulinId: bolus.insulinContext?.patientInsulinId ?? undefined,
        isBasalInsulin: false,
      };
    } else {
      editForm = {
        insulin: null,
        bolusType: undefined,
        programmed: undefined,
        delivered: undefined,
        duration: undefined,
        automatic: false,
        insulinType: "",
        patientInsulinId: undefined,
        isBasalInsulin: false,
      };
    }
    mode = "edit";
  }

  function returnToList() {
    mode = "list";
    editingBolus = null;
    deletingBolusId = null;
  }

  function handleSave() {
    if (!meal || !formRef) return;
    isSaving = true;
    formRef.requestSubmit();
  }

  async function handleDelete(bolus: Bolus) {
    const bolusId = bolus.id;
    if (!bolusId) return;
    await deletion.run(async () => {
      await removeBolus(bolusId);
      realtimeStore?.noteTreatmentWrite();
      toast.success("Bolus deleted");
      onSave();
      deletingBolusId = null;
    });
  }

  function formatBolusTime(mills: number | undefined): string {
    if (!mills) return "";
    return formatClock(mills);
  }
</script>

<!-- Hidden form for create/update submission -->
{#if mode === "edit" && meal}
  <form
    bind:this={formRef}
    class="hidden"
    {...activeForm.enhance(async ({ submit }) => {
      const submitted = await saving.run(submit);
      if (submitted && activeForm.result) {
        realtimeStore?.noteTreatmentWrite();
        toast.success(editingBolus ? "Bolus updated" : "Bolus added");
        onSave();
        returnToList();
      } else {
        toast.error(saving.error ?? "Failed to save bolus");
      }
      isSaving = false;
    })}
  >
    {#if editingBolus?.id}
      <input {...updateBolusForm.fields.id.as("hidden", editingBolus.id)} />
      {#if editingBolus.timestamp}
        <input {...updateBolusForm.fields.request.timestamp.as("hidden", editingBolus.timestamp)} />
      {/if}
      <input {...updateBolusForm.fields.request.insulin.as("hidden", editForm.insulin ?? 0)} />
      <input {...updateBolusForm.fields.request.automatic.as("hidden", editForm.automatic)} />
      {#if editForm.programmed != null}
        <input {...updateBolusForm.fields.request.programmed.as("hidden", editForm.programmed)} />
      {/if}
      {#if editForm.delivered != null}
        <input {...updateBolusForm.fields.request.delivered.as("hidden", editForm.delivered)} />
      {/if}
      {#if editForm.duration != null}
        <input {...updateBolusForm.fields.request.duration.as("hidden", editForm.duration)} />
      {/if}
      {#if editForm.insulinType}
        <input {...updateBolusForm.fields.request.insulinType.as("hidden", editForm.insulinType)} />
      {/if}
      {#if editForm.patientInsulinId}
        <input {...updateBolusForm.fields.request.patientInsulinId.as("hidden", editForm.patientInsulinId)} />
      {/if}
    {:else}
      <input {...createBolusForm.fields.timestamp.as("hidden", new Date(meal.carbIntakes?.[0]?.mills ?? Date.now()).toISOString())} />
      <input {...createBolusForm.fields.insulin.as("hidden", editForm.insulin ?? 0)} />
      <input {...createBolusForm.fields.kind.as("hidden", "Manual")} />
      <input {...createBolusForm.fields.automatic.as("hidden", editForm.automatic)} />
      {#if editForm.bolusType}
        <input {...createBolusForm.fields.bolusType.as("hidden", editForm.bolusType)} />
      {/if}
      {#if editForm.programmed != null}
        <input {...createBolusForm.fields.programmed.as("hidden", editForm.programmed)} />
      {/if}
      {#if editForm.delivered != null}
        <input {...createBolusForm.fields.delivered.as("hidden", editForm.delivered)} />
      {/if}
      {#if editForm.duration != null}
        <input {...createBolusForm.fields.duration.as("hidden", editForm.duration)} />
      {/if}
      {#if editForm.insulinType}
        <input {...createBolusForm.fields.insulinType.as("hidden", editForm.insulinType)} />
      {/if}
      {#if editForm.patientInsulinId}
        <input {...createBolusForm.fields.patientInsulinId.as("hidden", editForm.patientInsulinId)} />
      {/if}
      {#if meal.correlationId}
        <input {...createBolusForm.fields.correlationId.as("hidden", meal.correlationId)} />
      {/if}
    {/if}
  </form>
{/if}

<Dialog.Root bind:open {onOpenChange}>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      {#if mode === "edit"}
        <div class="flex items-center gap-2">
          <Button
            variant="ghost"
            size="icon-sm"
            onclick={returnToList}
          >
            <ArrowLeft class="h-4 w-4" />
          </Button>
          <Dialog.Title>
            {editingBolus ? "Edit bolus" : "Add bolus"}
          </Dialog.Title>
        </div>
      {:else}
        <Dialog.Title class="flex items-center gap-2">
          <Syringe class="h-5 w-5" />
          Insulin
        </Dialog.Title>
        {#if mealTimestamp}
          <Dialog.Description>{mealTimestamp}</Dialog.Description>
        {/if}
      {/if}
    </Dialog.Header>

    {#if mode === "list"}
      <div class="max-h-[60vh] space-y-2 overflow-y-auto">
        {#if meal?.boluses?.length}
          {#each meal.boluses as bolus (bolus.id)}
            <div
              class="flex items-center justify-between rounded-md border p-3"
            >
              {#if deletingBolusId === bolus.id}
                <div class="flex w-full items-center justify-between">
                  <span class="text-sm text-muted-foreground"
                    >Delete this bolus?</span
                  >
                  <div class="flex gap-2">
                    <Button
                      variant="ghost"
                      size="sm"
                      onclick={() => (deletingBolusId = null)}
                      disabled={deletion.busy}
                    >
                      Cancel
                    </Button>
                    <Button
                      variant="destructive"
                      size="sm"
                      onclick={() => handleDelete(bolus)}
                      disabled={deletion.busy}
                    >
                      Delete
                    </Button>
                  </div>
                </div>
              {:else}
                <div class="flex flex-col gap-1">
                  <div class="flex items-center gap-2">
                    <span class="text-sm font-medium">
                      {formatBolusTime(bolus.mills)}
                    </span>
                    <span class="text-sm">{formatInsulinDisplay(bolus.insulin)}U</span>
                    {#if bolus.bolusType}
                      <Badge variant="secondary">
                        {bolus.bolusType}
                      </Badge>
                    {/if}
                  </div>
                  {#if bolus.insulinContext?.insulinName}
                    <span class="text-xs text-muted-foreground">
                      {bolus.insulinContext.insulinName}
                    </span>
                  {/if}
                </div>
                <div class="flex gap-1">
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label="Edit bolus"
                    onclick={() => startEdit(bolus)}
                  >
                    <Pencil class="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label="Delete bolus"
                    onclick={() => (deletingBolusId = bolus.id ?? null)}
                  >
                    <Trash2 class="h-4 w-4" />
                  </Button>
                </div>
              {/if}
            </div>
          {/each}
        {:else}
          <p class="py-6 text-center text-sm text-muted-foreground">
            No insulin records for this meal
          </p>
        {/if}
      </div>

      {#if meal?.correlationId}
        <div class="pt-2">
          <Button
            variant="outline"
            class="w-full"
            onclick={() => startEdit(null)}
          >
            <Plus class="mr-2 h-4 w-4" />
            Add bolus
          </Button>
        </div>
      {:else}
        <p class="pt-2 text-center text-sm text-muted-foreground">
          This meal has no link to an insulin record. You cannot add insulin here.
        </p>
      {/if}
    {:else}
      <div class="space-y-4">
        <BolusFormFields bind:form={editForm} {patientInsulins} />
      </div>

      <div class="flex justify-end gap-2 pt-4">
        <Button variant="outline" onclick={returnToList} disabled={isSaving}>
          Cancel
        </Button>
        <Button onclick={handleSave} disabled={isSaving}>
          {isSaving ? "Saving..." : "Save"}
        </Button>
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>
