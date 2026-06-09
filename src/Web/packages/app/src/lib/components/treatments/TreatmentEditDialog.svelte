<script lang="ts">
  import type {
    Bolus,
    CarbIntake,
    BGCheck,
    Note,
    DeviceEvent,
    BolusType,
    GlucoseType,
    GlucoseUnit,
    DeviceEventType,
    PatientInsulin,
    InsulinFormulation,
    BasalInjection,
    CreateBasalInjectionRequest,
  } from "$lib/api";
  import { InsulinCategory as InsulinCategoryEnum } from "$lib/api";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    ENTRY_CATEGORIES,
    getEntryStyle,
  } from "$lib/constants/entry-categories";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Sheet from "$lib/components/ui/sheet";
  import { IsMobile } from "$lib/hooks/is-mobile.svelte";
  import { untrack } from "svelte";
  import {
    useDialogHistory,
    type DialogHistoryParam,
  } from "$lib/hooks/dialog-history.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Syringe,
    Apple,
    Droplet,
    FileText,
    Smartphone,
    Clock,
    Database,
    Trash2,
  } from "lucide-svelte";
  import { formatDateForInput, formatDateTimeCompact } from "$lib/utils/formatting";
  import * as patientRemote from "$api/generated/patientRecords.generated.remote";
  import { getCatalog } from "$api/generated/insulinCatalogs.generated.remote";
  import BolusFormFields from "./edit-dialog/BolusFormFields.svelte";
  import CarbsFormFields from "./edit-dialog/CarbsFormFields.svelte";
  import BGCheckFormFields from "./edit-dialog/BGCheckFormFields.svelte";
  import NoteFormFields from "./edit-dialog/NoteFormFields.svelte";
  import DeviceEventFormFields from "./edit-dialog/DeviceEventFormFields.svelte";
  import BasalInjectionFormFields from "./edit-dialog/BasalInjectionFormFields.svelte";
  import LinkedRecordsPanel from "./edit-dialog/LinkedRecordsPanel.svelte";
  import InsulinFormFields from "$lib/components/patient/InsulinFormFields.svelte";

  interface Props {
    open: boolean;
    record: EntryRecord | null;
    correlatedRecords?: EntryRecord[];
    isLoading?: boolean;
    onClose: () => void;
    onSave: (record: EntryRecord) => void;
    onDelete?: (record: EntryRecord) => void;
    /**
     * When set, the open dialog is reflected in this URL search param so it can
     * be reloaded / deep-linked (the owning page resolves the param on load).
     * Omit for plain back-button dismissal via an opaque history flag.
     */
    historyParam?: DialogHistoryParam;
  }

  let {
    open = $bindable(),
    record,
    correlatedRecords = [],
    isLoading = false,
    onClose,
    onSave,
    onDelete,
    historyParam,
  }: Props = $props();

  // On mobile, present the editor as a bottom sheet instead of a centered dialog.
  const isMobile = new IsMobile();

  // Let the browser back button (and mobile back gesture) dismiss the dialog.
  // `historyParam` is stable config supplied at mount, so reading it once is
  // intentional (untrack avoids the reactive-capture warning).
  useDialogHistory(
    () => open,
    () => onClose(),
    { param: untrack(() => historyParam) },
  );

  // Override record for viewing linked records (null = use the `record` prop)
  let overrideRecord = $state<EntryRecord | null>(null);

  // When the record prop changes (new dialog open), clear the override
  $effect(() => {
    record; // track the prop
    overrideRecord = null;
  });

  // The currently displayed record: override (linked record click) or the prop
  let activeRecord = $derived(overrideRecord ?? record);

  // Form states per kind
  let bolusForm = $state({
    insulin: 0 as number,
    bolusType: undefined as BolusType | undefined,
    programmed: undefined as number | undefined,
    delivered: undefined as number | undefined,
    duration: undefined as number | undefined,
    automatic: false,
    insulinType: "",
    patientInsulinId: undefined as string | undefined,
    isBasalInsulin: false,
  });

  let carbsForm = $state({
    carbs: 0 as number,
    absorptionTime: undefined as number | undefined,
    carbTime: undefined as number | undefined,
  });

  let bgCheckForm = $state({
    glucose: 0 as number,
    glucoseType: undefined as GlucoseType | undefined,
    units: undefined as GlucoseUnit | undefined,
  });

  let noteForm = $state({
    text: "",
    eventType: "",
    isAnnouncement: false,
  });

  let deviceEventForm = $state({
    eventType: undefined as DeviceEventType | undefined,
    notes: "",
  });

  let basalInjectionForm = $state<Partial<CreateBasalInjectionRequest>>({
    patientInsulinId: undefined,
    units: undefined,
    notes: "",
  });

  // Load patient insulins for the dropdown
  const insulinsResource = patientRemote.getInsulins();
  let patientInsulins = $derived((insulinsResource.current ?? []) as PatientInsulin[]);

  // Load insulin catalog for "add new" form
  const catalogResource = getCatalog(undefined);
  let catalog = $derived((catalogResource.current ?? []) as InsulinFormulation[]);

  // Inline "add new insulin" state
  let showAddInsulin = $state(false);
  let addCategory = $state("");
  let addFormulationId = $state("");
  let addName = $state("");
  let addRole = $state("");
  let addDia = $state<number | null>(4.0);
  let addPeak = $state<number | null>(75);
  let addCurve = $state("rapid-acting");
  let addConcentration = $state<number | null>(100);

  let addFormulations = $derived(
    addCategory ? catalog.filter((f) => f.category === addCategory) : []
  );

  function onAddCategoryChange() {
    addFormulationId = "";
    addName = "";
    const isLongActing = addCategory === InsulinCategoryEnum.LongActing
      || addCategory === InsulinCategoryEnum.UltraLongActing;
    addRole = isLongActing ? "Basal" : "Bolus";
    addDia = isLongActing ? 24.0 : 4.0;
    addPeak = isLongActing ? 0 : 75;
    addCurve = isLongActing ? "bilinear" : "rapid-acting";
    addConcentration = 100;
  }

  function onAddFormulationChange() {
    const formulation = catalog.find((f) => f.id === addFormulationId);
    if (formulation) {
      addName = formulation.name ?? "";
      addDia = formulation.defaultDia ?? 4.0;
      addPeak = formulation.defaultPeak ?? 75;
      addCurve = formulation.curve ?? "rapid-acting";
      addConcentration = formulation.concentration ?? 100;
    }
  }

  function resetAddForm() {
    showAddInsulin = false;
    addCategory = "";
    addFormulationId = "";
    addName = "";
    addRole = "";
    addDia = 4.0;
    addPeak = 75;
    addCurve = "rapid-acting";
    addConcentration = 100;
  }

  const addInsulinForm = patientRemote.createInsulin;
  let addSaving = $derived(!!addInsulinForm.pending);

  // Common timestamp field (mills)
  let editMills = $state<number>(Date.now());

  // Sync form state from activeRecord
  $effect(() => {
    if (!activeRecord) return;
    editMills = activeRecord.data.mills ?? Date.now();

    switch (activeRecord.kind) {
      case "bolus": {
        const d = activeRecord.data;
        bolusForm = {
          insulin: d.insulin ?? 0,
          bolusType: d.bolusType ?? undefined,
          programmed: d.programmed ?? undefined,
          delivered: d.delivered ?? undefined,
          duration: d.duration ?? undefined,
          automatic: d.automatic ?? false,
          insulinType: d.insulinType ?? "",
          patientInsulinId: d.insulinContext?.patientInsulinId ?? undefined,
          isBasalInsulin: (d.additionalProperties?.["isBasalInsulin"] as boolean) ?? false,
        };
        break;
      }
      case "carbs": {
        const d = activeRecord.data;
        carbsForm = {
          carbs: d.carbs ?? 0,
          absorptionTime: d.absorptionTime ?? undefined,
          carbTime: d.carbTime ?? undefined,
        };
        break;
      }
      case "bgCheck": {
        const d = activeRecord.data;
        bgCheckForm = {
          glucose: d.glucose ?? 0,
          glucoseType: d.glucoseType ?? undefined,
          units: d.units ?? undefined,
        };
        break;
      }
      case "note": {
        const d = activeRecord.data;
        noteForm = {
          text: d.text ?? "",
          eventType: d.eventType ?? "",
          isAnnouncement: d.isAnnouncement ?? false,
        };
        break;
      }
      case "deviceEvent": {
        const d = activeRecord.data;
        deviceEventForm = {
          eventType: d.eventType ?? undefined,
          notes: d.notes ?? "",
        };
        break;
      }
      case "basalInjection": {
        const d = activeRecord.data;
        basalInjectionForm = {
          patientInsulinId: d.insulinContext?.patientInsulinId ?? undefined,
          units: d.units ?? undefined,
          notes: d.notes ?? "",
        };
        break;
      }
    }
  });

  // Filter patientInsulins to those eligible for a basal injection at the
  // currently-edited timestamp: role in (Basal, Both) and active at that mills.
  let basalEligibleInsulins = $derived.by(() => {
    const ts = editMills;
    return patientInsulins.filter((i) => {
      if (i.role !== "Basal" && i.role !== "Both") return false;
      const start = i.startDate ? new Date(i.startDate).getTime() : -Infinity;
      const end = i.endDate ? new Date(i.endDate).getTime() : Infinity;
      return ts >= start && ts <= end;
    });
  });

  // Correlation group: all records sharing the same correlationId
  let correlationGroup = $derived.by(() => {
    if (!record) return [];
    const all = [record, ...correlatedRecords];
    // Deduplicate by id
    const seen = new Set<string>();
    return all.filter((r) => {
      const id = r.data.id;
      if (!id || seen.has(id)) return false;
      seen.add(id);
      return true;
    });
  });

  // Icon per kind
  const kindIcon = {
    bolus: Syringe,
    carbs: Apple,
    bgCheck: Droplet,
    note: FileText,
    deviceEvent: Smartphone,
    basalInjection: Syringe,
  };

  let activeCategory = $derived(
    activeRecord ? ENTRY_CATEGORIES[activeRecord.kind] : null
  );
  let activeStyle = $derived(
    activeRecord ? getEntryStyle(activeRecord.kind) : null
  );
  let ActiveKindIcon = $derived(
    activeRecord ? kindIcon[activeRecord.kind] : null
  );

  // A record without an id is being created rather than edited. Drives the
  // title/description/button copy and suppresses edit-only chrome.
  let isCreate = $derived(!activeRecord?.data.id);

  function handleSubmit() {
    if (!activeRecord) return;

    const baseData = {
      ...activeRecord.data,
      mills: editMills,
    };

    let updated: EntryRecord;

    switch (activeRecord.kind) {
      case "bolus":
        updated = {
          kind: "bolus",
          data: {
            ...baseData,
            ...bolusForm,
          } as Bolus,
        };
        break;
      case "carbs":
        updated = {
          kind: "carbs",
          data: {
            ...baseData,
            ...carbsForm,
          } as CarbIntake,
        };
        break;
      case "bgCheck":
        updated = {
          kind: "bgCheck",
          data: {
            ...baseData,
            ...bgCheckForm,
          } as BGCheck,
        };
        break;
      case "note":
        updated = {
          kind: "note",
          data: {
            ...baseData,
            ...noteForm,
          } as Note,
        };
        break;
      case "deviceEvent":
        updated = {
          kind: "deviceEvent",
          data: {
            ...baseData,
            ...deviceEventForm,
          } as DeviceEvent,
        };
        break;
      case "basalInjection": {
        const existingContext = activeRecord.data.insulinContext;
        const selectedInsulin = patientInsulins.find(
          (i) => i.id === basalInjectionForm.patientInsulinId
        );
        const nextContext = selectedInsulin
          ? {
              patientInsulinId: selectedInsulin.id,
              insulinName: selectedInsulin.name,
              dia: selectedInsulin.dia,
              peak: selectedInsulin.peak,
              curve: selectedInsulin.curve,
              concentration: selectedInsulin.concentration,
            }
          : existingContext;
        updated = {
          kind: "basalInjection",
          data: {
            ...baseData,
            units: basalInjectionForm.units ?? activeRecord.data.units,
            notes: basalInjectionForm.notes ?? "",
            insulinContext: nextContext,
          } as BasalInjection,
        };
        break;
      }
    }

    onSave(updated);
  }

  function switchToRecord(r: EntryRecord) {
    overrideRecord = r;
  }

  function formatMills(mills: number | undefined): string {
    if (!mills) return "\u2014";
    return formatDateTimeCompact(new Date(mills).toISOString());
  }

  function millsToInputValue(mills: number): string {
    return formatDateForInput(new Date(mills).toISOString());
  }
</script>

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
    <Dialog.Content class="max-w-lg max-h-[90vh] overflow-y-auto">
      {@render dialogBody()}
    </Dialog.Content>
  </Dialog.Root>
{/if}

{#snippet dialogBody()}
    {#if activeRecord && activeCategory && activeStyle && ActiveKindIcon}
      <Dialog.Header>
        <Dialog.Title class="flex items-center gap-2">
          <Badge
            variant="outline"
            class="{activeStyle.colorClass} {activeStyle.bgClass} {activeStyle.borderClass}"
          >
            <ActiveKindIcon class="mr-1 h-3.5 w-3.5" />
            {activeCategory.name}
          </Badge>
          {isCreate ? "Add Record" : "Edit Record"}
        </Dialog.Title>
        <Dialog.Description>
          {isCreate
            ? `Log a new ${activeCategory.name.toLowerCase()} record.`
            : `Edit the details of this ${activeCategory.name.toLowerCase()} record.`}
        </Dialog.Description>
      </Dialog.Header>

      <form
        onsubmit={(e) => {
          e.preventDefault();
          handleSubmit();
        }}
        class="space-y-4"
      >
        <!-- Read-only metadata -->
        {#if !isCreate}
        <div
          class="flex flex-wrap gap-4 text-sm text-muted-foreground bg-muted/30 rounded-lg p-3"
        >
          <div class="flex items-center gap-1.5">
            <Clock class="h-3.5 w-3.5" />
            <span>{formatMills(activeRecord.data.mills)}</span>
          </div>
          {#if activeRecord.data.device}
            <div class="flex items-center gap-1.5">
              <Smartphone class="h-3.5 w-3.5" />
              <span>{activeRecord.data.device}</span>
            </div>
          {/if}
          {#if activeRecord.data.dataSource}
            <div class="flex items-center gap-1.5">
              <Database class="h-3.5 w-3.5" />
              <span>{activeRecord.data.dataSource}</span>
            </div>
          {/if}
        </div>
        {/if}

        <!-- Date and Time -->
        <div class="space-y-2">
          <Label for="datetime">Date & Time</Label>
          <Input
            id="datetime"
            type="datetime-local"
            value={millsToInputValue(editMills)}
            onchange={(e: Event & { currentTarget: HTMLInputElement }) => {
              const val = e.currentTarget.value;
              if (val) editMills = new Date(val).getTime();
            }}
          />
        </div>

        <!-- Kind-specific form fields -->
        {#if activeRecord.kind === "bolus"}
          <BolusFormFields
            bind:form={bolusForm}
            {patientInsulins}
            onAddInsulin={() => showAddInsulin = !showAddInsulin}
          />
        {:else if activeRecord.kind === "carbs"}
          <CarbsFormFields bind:form={carbsForm} />
        {:else if activeRecord.kind === "bgCheck"}
          <BGCheckFormFields bind:form={bgCheckForm} />
        {:else if activeRecord.kind === "note"}
          <NoteFormFields bind:form={noteForm} />
        {:else if activeRecord.kind === "deviceEvent"}
          <DeviceEventFormFields bind:form={deviceEventForm} />
        {:else if activeRecord.kind === "basalInjection"}
          <BasalInjectionFormFields
            bind:value={basalInjectionForm}
            basalInsulins={basalEligibleInsulins}
          />
        {/if}

        <!-- Linked Records Panel -->
        <LinkedRecordsPanel
          records={correlationGroup}
          activeRecordId={activeRecord?.data.id ?? ""}
          onSwitch={switchToRecord}
        />

        <Dialog.Footer class="gap-2">
          {#if onDelete && activeRecord}
            <Button
              type="button"
              variant="destructive"
              onclick={() => activeRecord && onDelete(activeRecord)}
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
            {isLoading
              ? "Saving..."
              : isCreate
                ? "Create Record"
                : "Save Changes"}
          </Button>
        </Dialog.Footer>
      </form>

      {#if activeRecord?.kind === "bolus" && showAddInsulin}
        <form
          class="border rounded-lg p-4 space-y-4 bg-muted/30 mt-4"
          {...addInsulinForm.enhance(async ({ submit }) => {
            await submit();
            if (addInsulinForm.result) {
              const created = addInsulinForm.result as PatientInsulin;
              if (created?.id) {
                bolusForm.patientInsulinId = created.id;
                bolusForm.insulinType = created.name ?? "";
                if (created.role === "Basal" || created.role === "Both") {
                  bolusForm.isBasalInsulin = true;
                }
              }
              resetAddForm();
            }
          })}
        >
          <div class="flex items-center justify-between">
            <h4 class="text-sm font-medium">Add New Insulin</h4>
          </div>
          <div class="space-y-4">
            <InsulinFormFields
              bind:category={addCategory}
              bind:formulationId={addFormulationId}
              bind:name={addName}
              bind:role={addRole}
              formulations={addFormulations}
              {catalog}
              onCategoryChange={onAddCategoryChange}
              onFormulationChange={onAddFormulationChange}
            />

            <!-- Hidden fields for form submission -->
            <input type="hidden" name="b:isCurrent" value="on" />
            <input type="hidden" name="n:dia" value={addDia} />
            <input type="hidden" name="n:peak" value={addPeak} />
            <input type="hidden" name="curve" value={addCurve} />
            <input type="hidden" name="n:concentration" value={addConcentration} />
            {#if addFormulationId}
              <input type="hidden" name="formulationId" value={addFormulationId} />
            {/if}
          </div>

          <div class="flex gap-2 justify-end mt-4">
            <Button type="button" variant="ghost" size="sm" onclick={resetAddForm}>
              Cancel
            </Button>
            <Button
              type="submit"
              size="sm"
              disabled={!addCategory || !addName.trim() || addSaving}
            >
              {addSaving ? "Adding..." : "Add Insulin"}
            </Button>
          </div>
        </form>
      {/if}
    {:else}
      <Dialog.Header>
        <Dialog.Title>No Record Selected</Dialog.Title>
        <Dialog.Description>
          Click on a row in the table to view and edit its details.
        </Dialog.Description>
      </Dialog.Header>
    {/if}
{/snippet}
