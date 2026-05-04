<script lang="ts">
  import type { MealEvent, Bolus, BolusType, PatientInsulin } from "$lib/api";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import BolusFormFields from "$lib/components/treatments/edit-dialog/BolusFormFields.svelte";
  import * as patientRemote from "$api/generated/patientRecords.generated.remote";
  import {
    create as createBolusForm,
    update as updateBolusForm,
    remove as removeBolus,
  } from "$api/generated/bolus.generated.remote";
  import { toast } from "svelte-sonner";
  import { Syringe, Plus, Pencil, Trash2, ArrowLeft } from "lucide-svelte";

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
  let isDeleting = $state(false);

  let editForm = $state({
    insulin: null as number | null,
    bolusType: undefined as BolusType | undefined,
    programmed: undefined as number | undefined,
    delivered: undefined as number | undefined,
    duration: undefined as number | undefined,
    automatic: false,
    insulinType: "",
    patientInsulinId: undefined as string | undefined,
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
  let patientInsulins = $derived(
    (insulinsResource.current ?? []) as PatientInsulin[],
  );

  // Reset to list mode when dialog opens
  $effect(() => {
    if (open) {
      mode = "list";
      editingBolus = null;
      deletingBolusId = null;
    }
  });

  let mealTimestamp = $derived(
    meal?.timestamp
      ? new Date(meal.timestamp).toLocaleString(undefined, {
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
    if (!bolus.id) return;
    isDeleting = true;
    try {
      await removeBolus(bolus.id);
      toast.success("Bolus deleted");
      onSave();
      deletingBolusId = null;
    } catch {
      toast.error("Failed to delete bolus");
    } finally {
      isDeleting = false;
    }
  }

  function formatBolusTime(mills: number | undefined): string {
    if (!mills) return "";
    return new Date(mills).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
    });
  }
</script>

<!-- Hidden form for create/update submission -->
{#if mode === "edit" && meal}
  <form
    bind:this={formRef}
    class="hidden"
    {...activeForm.enhance(async ({ submit }) => {
      await submit();
      if (activeForm.result) {
        toast.success(editingBolus ? "Bolus updated" : "Bolus added");
        onSave();
        returnToList();
      } else {
        toast.error("Failed to save bolus");
      }
      isSaving = false;
    })}
  >
    {#if editingBolus?.id}
      <input type="hidden" name="id" value={editingBolus.id} />
      <input type="hidden" name="request.timestamp" value={editingBolus.timestamp} />
      <input type="hidden" name="request.insulin" value={editForm.insulin ?? 0} />
      <input type="hidden" name="request.automatic" value={editForm.automatic} />
      {#if editForm.programmed != null}
        <input type="hidden" name="request.programmed" value={editForm.programmed} />
      {/if}
      {#if editForm.delivered != null}
        <input type="hidden" name="request.delivered" value={editForm.delivered} />
      {/if}
      {#if editForm.duration != null}
        <input type="hidden" name="request.duration" value={editForm.duration} />
      {/if}
      {#if editForm.insulinType}
        <input type="hidden" name="request.insulinType" value={editForm.insulinType} />
      {/if}
      {#if editForm.patientInsulinId}
        <input type="hidden" name="request.patientInsulinId" value={editForm.patientInsulinId} />
      {/if}
    {:else}
      <input
        type="hidden"
        name="timestamp"
        value={new Date(meal.carbIntakes?.[0]?.mills ?? Date.now()).toISOString()}
      />
      <input type="hidden" name="insulin" value={editForm.insulin ?? 0} />
      <input type="hidden" name="kind" value="Manual" />
      <input type="hidden" name="automatic" value={editForm.automatic} />
      {#if editForm.bolusType}
        <input type="hidden" name="bolusType" value={editForm.bolusType} />
      {/if}
      {#if editForm.programmed != null}
        <input type="hidden" name="programmed" value={editForm.programmed} />
      {/if}
      {#if editForm.delivered != null}
        <input type="hidden" name="delivered" value={editForm.delivered} />
      {/if}
      {#if editForm.duration != null}
        <input type="hidden" name="duration" value={editForm.duration} />
      {/if}
      {#if editForm.insulinType}
        <input type="hidden" name="insulinType" value={editForm.insulinType} />
      {/if}
      {#if editForm.patientInsulinId}
        <input type="hidden" name="patientInsulinId" value={editForm.patientInsulinId} />
      {/if}
      {#if meal.correlationId}
        <input type="hidden" name="correlationId" value={meal.correlationId} />
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
            size="icon"
            class="h-8 w-8"
            onclick={returnToList}
          >
            <ArrowLeft class="h-4 w-4" />
          </Button>
          <Dialog.Title>
            {editingBolus ? "Edit Bolus" : "Add Bolus"}
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
                      disabled={isDeleting}
                    >
                      Cancel
                    </Button>
                    <Button
                      variant="destructive"
                      size="sm"
                      onclick={() => handleDelete(bolus)}
                      disabled={isDeleting}
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
                    <span class="text-sm">{bolus.insulin}U</span>
                    {#if bolus.bolusType}
                      <Badge variant="secondary" class="text-xs">
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
                    size="icon"
                    class="h-8 w-8"
                    onclick={() => startEdit(bolus)}
                  >
                    <Pencil class="h-4 w-4" />
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon"
                    class="h-8 w-8"
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
