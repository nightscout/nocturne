<script lang="ts">
  import type { Bolus, CarbIntake, BGCheck, Note, DeviceEvent } from "$lib/api";
  import type { EntryRecord, EntryCategoryId } from "$lib/constants/entry-categories";
  import { ENTRY_CATEGORIES } from "$lib/constants/entry-categories";
  import * as Dialog from "$lib/components/ui/dialog";
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
  import { toast } from "svelte-sonner";
  import {
    createBolus,
    updateBolus,
    deleteBolus,
  } from "$api/generated/insulins.generated.remote";
  import {
    createCarbIntake,
    updateCarbIntake,
    deleteCarbIntake,
    addCarbIntakeFood,
  } from "$api/generated/nutritions.generated.remote";
  import {
    createBGCheck,
    updateBGCheck,
    deleteBGCheck,
    createNote,
    updateNote,
    deleteNote,
    createDeviceEvent,
    updateDeviceEvent,
    deleteDeviceEvent,
  } from "$api/generated/observations.generated.remote";

  interface Sections {
    bolus: Partial<Bolus> | null;
    carbs: Partial<CarbIntake> | null;
    bgCheck: Partial<BGCheck> | null;
    note: Partial<Note> | null;
    deviceEvent: Partial<DeviceEvent> | null;
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

  let sections = $state<Sections>({
    bolus: null,
    carbs: null,
    bgCheck: null,
    note: null,
    deviceEvent: null,
  });

  let mills = $state<number>(Date.now());
  let isSaving = $state(false);
  let isDeleting = $state(false);
  let carbsPendingFoods = $state<PendingFood[]>([]);

  let isEditing = $derived(entry != null);

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

  const sectionIcons: Record<EntryCategoryId, typeof Syringe> = {
    bolus: Syringe,
    carbs: Apple,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
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

  async function handleSave() {
    isSaving = true;
    try {
      const correlationId =
        activeSectionCount > 1 ? crypto.randomUUID() : undefined;

      const promises: Promise<unknown>[] = [];

      if (sections.bolus != null) {
        const data: Partial<Bolus> = {
          ...sections.bolus,
          mills,
          correlationId,
        };
        const existing = findExistingRecord("bolus");
        if (existing?.data.id) {
          promises.push(
            updateBolus({ id: existing.data.id, request: data as Bolus }),
          );
        } else {
          promises.push(createBolus(data as Bolus));
        }
      }

      if (sections.carbs != null) {
        const data: Partial<CarbIntake> = {
          ...sections.carbs,
          mills,
          correlationId,
        };
        const existing = findExistingRecord("carbs");
        if (existing?.data.id) {
          promises.push(
            updateCarbIntake({
              id: existing.data.id,
              request: data as CarbIntake,
            }),
          );
        } else if (carbsPendingFoods.length > 0) {
          // Create carb intake first, then add pending foods
          promises.push(
            createCarbIntake(data as CarbIntake).then(async (result) => {
              if (!result?.id) return;
              for (const pf of carbsPendingFoods) {
                await addCarbIntakeFood({ id: result.id, request: pf.request });
              }
            }),
          );
        } else {
          promises.push(createCarbIntake(data as CarbIntake));
        }
      }

      if (sections.bgCheck != null) {
        const data: Partial<BGCheck> = {
          ...sections.bgCheck,
          mills,
          correlationId,
        };
        const existing = findExistingRecord("bgCheck");
        if (existing?.data.id) {
          promises.push(
            updateBGCheck({
              id: existing.data.id,
              request: data as BGCheck,
            }),
          );
        } else {
          promises.push(createBGCheck(data as BGCheck));
        }
      }

      if (sections.note != null) {
        const data: Partial<Note> = {
          ...sections.note,
          mills,
          correlationId,
        };
        const existing = findExistingRecord("note");
        if (existing?.data.id) {
          promises.push(
            updateNote({ id: existing.data.id, request: data as Note }),
          );
        } else {
          promises.push(createNote(data as Note));
        }
      }

      if (sections.deviceEvent != null) {
        const data: Partial<DeviceEvent> = {
          ...sections.deviceEvent,
          mills,
          correlationId,
        };
        const existing = findExistingRecord("deviceEvent");
        if (existing?.data.id) {
          promises.push(
            updateDeviceEvent({
              id: existing.data.id,
              request: data as DeviceEvent,
            }),
          );
        } else {
          promises.push(createDeviceEvent(data as DeviceEvent));
        }
      }

      await Promise.all(promises);
      toast.success(isEditing ? "Entry updated" : "Entry created");
      open = false;
      onClose();
    } catch (err) {
      console.error("Failed to save entry:", err);
      toast.error("Failed to save entry");
    } finally {
      isSaving = false;
    }
  }

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

<Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
  <Dialog.Content class="max-w-lg max-h-[85vh] overflow-y-auto">
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

    <form
      onsubmit={(e) => {
        e.preventDefault();
        handleSave();
      }}
      class="space-y-4"
    >
      <!-- Shared Timestamp -->
      <div class="space-y-1.5">
        <Label for="entry-timestamp">Date & Time</Label>
        <Input
          id="entry-timestamp"
          type="datetime-local"
          value={millsToInputValue(mills)}
          onchange={(e) => {
            const val = e.currentTarget.value;
            if (val) mills = inputValueToMills(val);
          }}
        />
      </div>

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
        {/if}
      {/each}

      <!-- Add Section Dropdown -->
      {#if inactiveSectionKeys.length > 0}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
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
        <Button type="submit" disabled={isSaving || isDeleting}>
          {#if isSaving}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Saving...
          {:else}
            {isEditing ? "Save Changes" : "Create"}
          {/if}
        </Button>
      </Dialog.Footer>
    </form>
  </Dialog.Content>
</Dialog.Root>
