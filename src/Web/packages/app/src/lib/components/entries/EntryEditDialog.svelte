<script lang="ts">
  import type { Bolus, CarbIntake, BGCheck, Note, DeviceEvent, BasalInjection } from "$lib/api";
  import { BolusType, GlucoseType, GlucoseUnit, DeviceEventType } from "$lib/api";
  import type { EntryRecord, EntryCategoryId } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Sheet from "$lib/components/ui/sheet";
  import { IsMobile } from "$lib/hooks/is-mobile.svelte";
  import { useDialogHistory } from "$lib/hooks/dialog-history.svelte";
  import * as DropdownMenu from "$lib/components/ui/dropdown-menu";
  import { Separator } from "$lib/components/ui/separator";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import BolusSection from "./BolusSection.svelte";
  import CarbIntakeSection, { type PendingFood } from "./CarbIntakeSection.svelte";
  import BGCheckSection from "./BGCheckSection.svelte";
  import NoteSection from "./NoteSection.svelte";
  import DeviceEventSection from "./DeviceEventSection.svelte";
  import BasalInjectionSection from "./BasalInjectionSection.svelte";
  import {
    Plus,
    Trash2,
    Loader2,
    Syringe,
    Apple,
    Droplet,
    FileText,
    Smartphone,
  } from "lucide-svelte";
  import { getDataSourceDisplayName } from "$lib/utils/data-source-display";
  import { toast } from "svelte-sonner";
  import {
    create as createBolusForm,
    update as updateBolusForm,
    remove as deleteBolus,
  } from "$api/generated/bolus.generated.remote";
  import {
    createCarbIntake as createCarbIntakeForm,
    updateCarbIntake as updateCarbIntakeForm,
    deleteCarbIntake,
    addCarbIntakeFood,
  } from "$api/generated/nutritions.generated.remote";
  import {
    create as createBGCheckForm,
    update as updateBGCheckForm,
    remove as deleteBGCheck,
  } from "$api/generated/bgChecks.generated.remote";
  import {
    create as createNoteForm,
    update as updateNoteForm,
    remove as deleteNote,
  } from "$api/generated/notes.generated.remote";
  import {
    create as createDeviceEventForm,
    update as updateDeviceEventForm,
    remove as deleteDeviceEvent,
  } from "$api/generated/deviceEvents.generated.remote";
  import {
    create as createBasalInjectionForm,
    update as updateBasalInjectionForm,
    remove as deleteBasalInjection,
  } from "$api/generated/basalInjections.generated.remote";

  interface Sections {
    bolus: Partial<Bolus> | null;
    carbs: Partial<CarbIntake> | null;
    bgCheck: Partial<BGCheck> | null;
    note: Partial<Note> | null;
    deviceEvent: Partial<DeviceEvent> | null;
    basalInjection: Partial<BasalInjection> | null;
  }

  interface Props {
    open: boolean;
    entry?: EntryRecord | null;
    correlatedRecords?: EntryRecord[];
    onClose: () => void;
  }

  let {
    open = $bindable(),
    entry = null,
    correlatedRecords = [],
    onClose,
  }: Props = $props();

  // On mobile, present the editor as a bottom sheet instead of a centered dialog.
  const isMobile = new IsMobile();

  // Let the browser back button (and mobile back gesture) dismiss the dialog.
  useDialogHistory(
    () => open,
    () => onClose(),
  );

  let sections = $state<Sections>({
    bolus: null,
    carbs: null,
    bgCheck: null,
    note: null,
    deviceEvent: null,
    basalInjection: null,
  });

  let mills = $state<number>(Date.now());
  let isDeleting = $state(false);
  let carbsPendingFoods = $state<PendingFood[]>([]);

  // Form element refs for programmatic submission
  let bolusFormRef = $state<HTMLFormElement | null>(null);
  let carbsFormRef = $state<HTMLFormElement | null>(null);
  let bgCheckFormRef = $state<HTMLFormElement | null>(null);
  let noteFormRef = $state<HTMLFormElement | null>(null);
  let deviceEventFormRef = $state<HTMLFormElement | null>(null);
  let basalInjectionFormRef = $state<HTMLFormElement | null>(null);

  // Track form completion per section
  let bolusFormDone = $state(false);
  let carbsFormDone = $state(false);
  let bgCheckFormDone = $state(false);
  let noteFormDone = $state(false);
  let deviceEventFormDone = $state(false);
  let basalInjectionFormDone = $state(false);
  let saveError = $state<string | null>(null);
  let isSaving = $state(false);

  let isEditing = $derived(entry != null);
  let sourceDisplayName = $derived(
    entry ? getDataSourceDisplayName(entry.data.dataSource ?? entry.data.device) : null,
  );

  let activeSectionCount = $derived(
    Object.values(sections).filter((s) => s != null).length,
  );

  let activeSectionKeys = $derived(
    (Object.keys(sections) as EntryCategoryId[]).filter(
      (k) => sections[k] != null,
    ),
  );

  let inactiveSectionKeys = $derived(
    (Object.keys(ENTRY_CATEGORIES) as EntryCategoryId[]).filter(
      (k) => sections[k] == null,
    ),
  );

  // Determine which form to use per section (create vs update)
  const existingBolusRecord = $derived(findExistingRecord("bolus"));
  const activeBolusForm = $derived(
    existingBolusRecord?.data.id ? updateBolusForm : createBolusForm,
  );

  const existingCarbsRecord = $derived(findExistingRecord("carbs"));
  const activeCarbsForm = $derived(
    existingCarbsRecord?.data.id ? updateCarbIntakeForm : createCarbIntakeForm,
  );

  const existingBGCheckRecord = $derived(findExistingRecord("bgCheck"));
  const activeBGCheckForm = $derived(
    existingBGCheckRecord?.data.id ? updateBGCheckForm : createBGCheckForm,
  );

  const existingNoteRecord = $derived(findExistingRecord("note"));
  const activeNoteForm = $derived(
    existingNoteRecord?.data.id ? updateNoteForm : createNoteForm,
  );

  const existingDeviceEventRecord = $derived(findExistingRecord("deviceEvent"));
  const activeDeviceEventForm = $derived(
    existingDeviceEventRecord?.data.id ? updateDeviceEventForm : createDeviceEventForm,
  );

  const existingBasalInjectionRecord = $derived(findExistingRecord("basalInjection"));
  const activeBasalInjectionForm = $derived(
    existingBasalInjectionRecord?.data.id ? updateBasalInjectionForm : createBasalInjectionForm,
  );

  // Aggregate pending state across all forms
  const formsPending = $derived(
    !!createBolusForm.pending ||
    !!updateBolusForm.pending ||
    !!createCarbIntakeForm.pending ||
    !!updateCarbIntakeForm.pending ||
    !!createBGCheckForm.pending ||
    !!updateBGCheckForm.pending ||
    !!createNoteForm.pending ||
    !!updateNoteForm.pending ||
    !!createDeviceEventForm.pending ||
    !!updateDeviceEventForm.pending ||
    !!createBasalInjectionForm.pending ||
    !!updateBasalInjectionForm.pending,
  );

  const sectionIcons: Record<EntryCategoryId, typeof Syringe> = {
    bolus: Syringe,
    carbs: Apple,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
    basalInjection: Syringe,
  };

  // Populate sections from entry and correlated records when dialog opens
  $effect(() => {
    if (!open) return;

    if (entry) {
      // Reset all sections
      const fresh: Sections = {
        bolus: null,
        carbs: null,
        bgCheck: null,
        note: null,
        deviceEvent: null,
        basalInjection: null,
      };

      // Populate primary entry
      populateSection(fresh, entry);

      // Populate correlated records
      for (const record of correlatedRecords) {
        populateSection(fresh, record);
      }

      sections = fresh;
      mills = entry.data.mills ?? Date.now();
      carbsPendingFoods = [];
    } else {
      // New entry: default to Meal Bolus layout (bolus + carbs)
      sections = {
        bolus: {},
        carbs: {},
        bgCheck: null,
        note: null,
        deviceEvent: null,
        basalInjection: null,
      };
      mills = Date.now();
      carbsPendingFoods = [];
    }
  });

  function populateSection(target: Sections, record: EntryRecord) {
    switch (record.kind) {
      case "bolus":
        target.bolus = { ...record.data };
        break;
      case "carbs":
        target.carbs = { ...record.data };
        break;
      case "bgCheck":
        target.bgCheck = { ...record.data };
        break;
      case "note":
        target.note = { ...record.data };
        break;
      case "deviceEvent":
        target.deviceEvent = { ...record.data };
        break;
      case "basalInjection":
        target.basalInjection = { ...record.data };
        break;
    }
  }

  function addSection(key: EntryCategoryId) {
    sections[key] = {};
  }

  function removeSection(key: EntryCategoryId) {
    if (activeSectionCount <= 1) return;
    sections[key] = null;
  }

  function millsToInputValue(ms: number): string {
    const date = new Date(ms);
    const year = date.getFullYear();
    const month = (date.getMonth() + 1).toString().padStart(2, "0");
    const day = date.getDate().toString().padStart(2, "0");
    const hours = date.getHours().toString().padStart(2, "0");
    const minutes = date.getMinutes().toString().padStart(2, "0");
    return `${year}-${month}-${day}T${hours}:${minutes}`;
  }

  function inputValueToMills(value: string): number {
    return new Date(value).getTime();
  }

  /** Find the existing record that matches a section, if editing */
  function findExistingRecord(
    kind: EntryCategoryId,
  ): EntryRecord | undefined {
    if (entry?.kind === kind) return entry;
    return correlatedRecords.find((r) => r.kind === kind);
  }

  function handleSave() {
    isSaving = true;
    saveError = null;

    // Mark inactive sections as done immediately
    bolusFormDone = sections.bolus == null;
    carbsFormDone = sections.carbs == null;
    bgCheckFormDone = sections.bgCheck == null;
    noteFormDone = sections.note == null;
    deviceEventFormDone = sections.deviceEvent == null;
    basalInjectionFormDone = sections.basalInjection == null;

    // Submit all active forms
    if (sections.bolus != null && bolusFormRef) {
      bolusFormRef.requestSubmit();
    }
    if (sections.carbs != null && carbsFormRef) {
      carbsFormRef.requestSubmit();
    }
    if (sections.bgCheck != null && bgCheckFormRef) {
      bgCheckFormRef.requestSubmit();
    }
    if (sections.note != null && noteFormRef) {
      noteFormRef.requestSubmit();
    }
    if (sections.deviceEvent != null && deviceEventFormRef) {
      deviceEventFormRef.requestSubmit();
    }
    if (sections.basalInjection != null && basalInjectionFormRef) {
      basalInjectionFormRef.requestSubmit();
    }
  }

  function checkAllDone() {
    if (bolusFormDone && carbsFormDone && bgCheckFormDone && noteFormDone && deviceEventFormDone && basalInjectionFormDone) {
      if (!saveError) {
        toast.success(isEditing ? "Entry updated" : "Entry created");
        open = false;
        onClose();
      } else {
        toast.error("Failed to save entry");
      }
      isSaving = false;
    }
  }

  // Watch for form completion
  $effect(() => {
    if (isSaving && bolusFormDone && carbsFormDone && bgCheckFormDone && noteFormDone && deviceEventFormDone && basalInjectionFormDone) {
      checkAllDone();
    }
  });

  async function handleDelete() {
    if (!entry?.data.id) return;
    isDeleting = true;
    try {
      const promises: Promise<unknown>[] = [];

      // Delete primary entry
      switch (entry.kind) {
        case "bolus":
          promises.push(deleteBolus(entry.data.id));
          break;
        case "carbs":
          promises.push(deleteCarbIntake(entry.data.id));
          break;
        case "bgCheck":
          promises.push(deleteBGCheck(entry.data.id));
          break;
        case "note":
          promises.push(deleteNote(entry.data.id));
          break;
        case "deviceEvent":
          promises.push(deleteDeviceEvent(entry.data.id));
          break;
        case "basalInjection":
          promises.push(deleteBasalInjection(entry.data.id));
          break;
      }

      // Delete correlated records
      for (const record of correlatedRecords) {
        if (!record.data.id) continue;
        switch (record.kind) {
          case "bolus":
            promises.push(deleteBolus(record.data.id));
            break;
          case "carbs":
            promises.push(deleteCarbIntake(record.data.id));
            break;
          case "bgCheck":
            promises.push(deleteBGCheck(record.data.id));
            break;
          case "note":
            promises.push(deleteNote(record.data.id));
            break;
          case "deviceEvent":
            promises.push(deleteDeviceEvent(record.data.id));
            break;
          case "basalInjection":
            promises.push(deleteBasalInjection(record.data.id));
            break;
        }
      }

      await Promise.all(promises);
      toast.success("Entry deleted");
      open = false;
      onClose();
    } catch (err) {
      console.error("Failed to delete entry:", err);
      toast.error("Failed to delete entry");
    } finally {
      isDeleting = false;
    }
  }
</script>

<!-- Hidden bolus form -->
{#if sections.bolus != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={bolusFormRef}
    class="hidden"
    {...activeBolusForm.enhance(async ({ submit }) => {
      await submit();
      if (activeBolusForm.result) {
        bolusFormDone = true;
      } else {
        saveError = "Failed to save bolus";
        bolusFormDone = true;
      }
    })}
  >
    {#if existingBolusRecord?.data.id}
      <input type="hidden" name="id" value={existingBolusRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    <input type="hidden" name="n:insulin" value={sections.bolus?.insulin ?? 0} />
    <input type="hidden" name="bolusType" value={sections.bolus?.bolusType ?? BolusType.Normal} />
    {#if sections.bolus?.duration != null}
      <input type="hidden" name="n:duration" value={sections.bolus.duration} />
    {/if}
    {#if sections.bolus?.programmed != null}
      <input type="hidden" name="n:programmed" value={sections.bolus.programmed} />
    {/if}
    {#if sections.bolus?.delivered != null}
      <input type="hidden" name="n:delivered" value={sections.bolus.delivered} />
    {/if}
    {#if sections.bolus?.insulinType}
      <input type="hidden" name="insulinType" value={sections.bolus.insulinType} />
    {/if}
    {#if sections.bolus?.unabsorbed != null}
      <input type="hidden" name="n:unabsorbed" value={sections.bolus.unabsorbed} />
    {/if}
  </form>
{/if}

<!-- Hidden carbs form -->
{#if sections.carbs != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={carbsFormRef}
    class="hidden"
    {...activeCarbsForm.enhance(async ({ submit }) => {
      await submit();
      const result = activeCarbsForm.result;
      if (result) {
        // If creating with pending foods, add them
        if (!existingCarbsRecord?.data.id && carbsPendingFoods.length > 0) {
          const newId = (result as any)?.id;
          if (newId) {
            for (const pf of carbsPendingFoods) {
              await addCarbIntakeFood({ id: newId, request: pf.request });
            }
          }
        }
        carbsFormDone = true;
      } else {
        saveError = "Failed to save carb intake";
        carbsFormDone = true;
      }
    })}
  >
    {#if existingCarbsRecord?.data.id}
      <input type="hidden" name="id" value={existingCarbsRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    <input type="hidden" name="n:carbs" value={sections.carbs?.carbs ?? 0} />
    {#if sections.carbs?.carbTime != null}
      <input type="hidden" name="n:carbTime" value={sections.carbs.carbTime} />
    {/if}
    {#if sections.carbs?.absorptionTime != null}
      <input type="hidden" name="n:absorptionTime" value={sections.carbs.absorptionTime} />
    {/if}
  </form>
{/if}

<!-- Hidden BG check form -->
{#if sections.bgCheck != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={bgCheckFormRef}
    class="hidden"
    {...activeBGCheckForm.enhance(async ({ submit }) => {
      await submit();
      if (activeBGCheckForm.result) {
        bgCheckFormDone = true;
      } else {
        saveError = "Failed to save BG check";
        bgCheckFormDone = true;
      }
    })}
  >
    {#if existingBGCheckRecord?.data.id}
      <input type="hidden" name="id" value={existingBGCheckRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    <input type="hidden" name="n:glucose" value={sections.bgCheck?.glucose ?? 0} />
    <input type="hidden" name="glucoseType" value={sections.bgCheck?.glucoseType ?? GlucoseType.Finger} />
    <input type="hidden" name="units" value={sections.bgCheck?.units ?? GlucoseUnit.MgDl} />
  </form>
{/if}

<!-- Hidden note form -->
{#if sections.note != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={noteFormRef}
    class="hidden"
    {...activeNoteForm.enhance(async ({ submit }) => {
      await submit();
      if (activeNoteForm.result) {
        noteFormDone = true;
      } else {
        saveError = "Failed to save note";
        noteFormDone = true;
      }
    })}
  >
    {#if existingNoteRecord?.data.id}
      <input type="hidden" name="id" value={existingNoteRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    <input type="hidden" name="text" value={sections.note?.text ?? ""} />
    <input type="hidden" name="isAnnouncement" value={sections.note?.isAnnouncement ?? false} />
  </form>
{/if}

<!-- Hidden device event form -->
{#if sections.deviceEvent != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={deviceEventFormRef}
    class="hidden"
    {...activeDeviceEventForm.enhance(async ({ submit }) => {
      await submit();
      if (activeDeviceEventForm.result) {
        deviceEventFormDone = true;
      } else {
        saveError = "Failed to save device event";
        deviceEventFormDone = true;
      }
    })}
  >
    {#if existingDeviceEventRecord?.data.id}
      <input type="hidden" name="id" value={existingDeviceEventRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    <input type="hidden" name="eventType" value={sections.deviceEvent?.eventType ?? DeviceEventType.SiteChange} />
    {#if sections.deviceEvent?.notes}
      <input type="hidden" name="notes" value={sections.deviceEvent.notes} />
    {/if}
  </form>
{/if}

<!-- Hidden basal injection form -->
{#if sections.basalInjection != null}
  {@const correlationId = activeSectionCount > 1 ? crypto.randomUUID() : undefined}
  <form
    bind:this={basalInjectionFormRef}
    class="hidden"
    {...activeBasalInjectionForm.enhance(async ({ submit }) => {
      await submit();
      if (activeBasalInjectionForm.result) {
        basalInjectionFormDone = true;
      } else {
        saveError = "Failed to save basal injection";
        basalInjectionFormDone = true;
      }
    })}
  >
    {#if existingBasalInjectionRecord?.data.id}
      <input type="hidden" name="id" value={existingBasalInjectionRecord.data.id} />
    {/if}
    <input type="hidden" name="n:mills" value={mills} />
    {#if correlationId}
      <input type="hidden" name="correlationId" value={correlationId} />
    {/if}
    {#if sections.basalInjection?.insulinContext?.patientInsulinId}
      <input
        type="hidden"
        name="patientInsulinId"
        value={sections.basalInjection.insulinContext.patientInsulinId}
      />
    {/if}
    <input type="hidden" name="n:units" value={sections.basalInjection?.units ?? 0} />
    {#if sections.basalInjection?.notes}
      <input type="hidden" name="notes" value={sections.basalInjection.notes} />
    {/if}
  </form>
{/if}

{#if isMobile.current}
  <Sheet.Root bind:open onOpenChange={(o) => !o && onClose()}>
    <Sheet.Content
      side="bottom"
      class="max-h-[90vh] overflow-y-auto rounded-t-xl p-6"
    >
      {@render dialogBody()}
    </Sheet.Content>
  </Sheet.Root>
{:else}
  <Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
    <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto">
      {@render dialogBody()}
    </Dialog.Content>
  </Dialog.Root>
{/if}

{#snippet dialogBody()}
    <Dialog.Header>
      <Dialog.Title>
        {isEditing ? "Edit Entry" : "New Entry"}
      </Dialog.Title>
      <Dialog.Description>
        {isEditing
          ? "Edit the details of this entry and its correlated records."
          : "Create a new entry. Add multiple record types to correlate them."}
      </Dialog.Description>
    </Dialog.Header>

    <div class="space-y-4">
      <!-- Shared Timestamp -->
      <div class="space-y-1.5">
        <Label for="entry-timestamp">Date & Time</Label>
        <Input
          id="entry-timestamp"
          type="datetime-local"
          value={millsToInputValue(mills)}
          onchange={(e: Event & { currentTarget: HTMLInputElement }) => {
            const val = e.currentTarget.value;
            if (val) mills = inputValueToMills(val);
          }}
        />
      </div>

      {#if isEditing && sourceDisplayName}
        <div class="flex items-center gap-2 text-sm">
          <span class="text-muted-foreground">Source:</span>
          <span class="font-medium">{sourceDisplayName}</span>
        </div>
      {/if}

      <Separator />

      <!-- Active Sections -->
      {#each activeSectionKeys as key, i (key)}
        {#if i > 0}
          <Separator />
        {/if}

        {#if key === "bolus" && sections.bolus != null}
          <BolusSection
            bind:bolus={sections.bolus}
            onRemove={activeSectionCount > 1 ? () => removeSection("bolus") : undefined}
          />
        {:else if key === "carbs" && sections.carbs != null}
          <CarbIntakeSection
            bind:carbIntake={sections.carbs}
            carbIntakeId={findExistingRecord("carbs")?.data.id}
            bind:pendingFoods={carbsPendingFoods}
            onRemove={activeSectionCount > 1 ? () => removeSection("carbs") : undefined}
          />
        {:else if key === "bgCheck" && sections.bgCheck != null}
          <BGCheckSection
            bind:bgCheck={sections.bgCheck}
            onRemove={activeSectionCount > 1 ? () => removeSection("bgCheck") : undefined}
          />
        {:else if key === "note" && sections.note != null}
          <NoteSection
            bind:note={sections.note}
            onRemove={activeSectionCount > 1 ? () => removeSection("note") : undefined}
          />
        {:else if key === "deviceEvent" && sections.deviceEvent != null}
          <DeviceEventSection
            bind:deviceEvent={sections.deviceEvent}
            onRemove={activeSectionCount > 1
              ? () => removeSection("deviceEvent")
              : undefined}
          />
        {:else if key === "basalInjection" && sections.basalInjection != null}
          <BasalInjectionSection
            bind:injection={sections.basalInjection}
            onRemove={activeSectionCount > 1
              ? () => removeSection("basalInjection")
              : undefined}
          />
        {/if}
      {/each}

      <!-- Add Section Dropdown -->
      {#if inactiveSectionKeys.length > 0}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props }: { props: Record<string, unknown> })}
              <Button {...props} variant="outline" size="sm" class="w-full">
                <Plus class="mr-2 h-4 w-4" />
                Add Section
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="start">
            {#each inactiveSectionKeys as key (key)}
              {@const category = ENTRY_CATEGORIES[key]}
              {@const Icon = sectionIcons[key]}
              <DropdownMenu.Item onclick={() => addSection(key)}>
                <Icon class="mr-2 h-4 w-4" />
                {category.name}
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {/if}

      <!-- Footer -->
      <Dialog.Footer class="gap-2">
        {#if isEditing && entry?.data.id}
          <Button
            type="button"
            variant="destructive"
            onclick={handleDelete}
            disabled={isSaving || isDeleting}
            class="mr-auto"
          >
            {#if isDeleting}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              Deleting...
            {:else}
              <Trash2 class="mr-2 h-4 w-4" />
              Delete
            {/if}
          </Button>
        {/if}
        <Button
          type="button"
          variant="outline"
          onclick={onClose}
          disabled={isSaving || isDeleting}
        >
          Cancel
        </Button>
        <Button
          type="button"
          onclick={handleSave}
          disabled={isSaving || isDeleting || formsPending}
        >
          {#if isSaving || formsPending}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Saving...
          {:else}
            {isEditing ? "Save Changes" : "Create"}
          {/if}
        </Button>
      </Dialog.Footer>
    </div>
{/snippet}
