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
  } from "$lib/api";
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    ENTRY_CATEGORIES,
    getEntryStyle,
  } from "$lib/constants/entry-categories";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Select from "$lib/components/ui/select";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Badge } from "$lib/components/ui/badge";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Syringe,
    Apple,
    Droplet,
    FileText,
    Smartphone,
    Clock,
    Database,
    Trash2,
    Link,
  } from "lucide-svelte";
  import {
    formatDateForInput,
    formatDateTimeCompact,
    formatInsulinDisplay,
    formatCarbDisplay,
  } from "$lib/utils/formatting";

  interface Props {
    open: boolean;
    record: EntryRecord | null;
    correlatedRecords?: EntryRecord[];
    isLoading?: boolean;
    onClose: () => void;
    onSave: (record: EntryRecord) => void;
    onDelete?: (record: EntryRecord) => void;
  }

  let {
    open = $bindable(),
    record,
    correlatedRecords = [],
    isLoading = false,
    onClose,
    onSave,
    onDelete,
  }: Props = $props();

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
    isBasalInsulin: false,
  });

  let carbsForm = $state({
    carbs: 0 as number,
    protein: undefined as number | undefined,
    fat: undefined as number | undefined,
    foodType: "",
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
          isBasalInsulin: d.isBasalInsulin ?? false,
        };
        break;
      }
      case "carbs": {
        const d = activeRecord.data;
        carbsForm = {
          carbs: d.carbs ?? 0,
          protein: d.protein ?? undefined,
          fat: d.fat ?? undefined,
          foodType: d.foodType ?? "",
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
    }
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
    }

    onSave(updated);
  }

  function switchToRecord(r: EntryRecord) {
    overrideRecord = r;
  }

  function getPrimaryValue(r: EntryRecord): string {
    switch (r.kind) {
      case "bolus":
        return r.data.insulin != null
          ? `${formatInsulinDisplay(r.data.insulin)}U`
          : "\u2014";
      case "carbs":
        return r.data.carbs != null
          ? `${formatCarbDisplay(r.data.carbs)}g`
          : "\u2014";
      case "bgCheck":
        return r.data.mgdl != null ? `${r.data.mgdl} mg/dL` : "\u2014";
      case "note":
        return r.data.text || "\u2014";
      case "deviceEvent":
        return r.data.eventType ?? "\u2014";
    }
  }

  function formatMills(mills: number | undefined): string {
    if (!mills) return "\u2014";
    return formatDateTimeCompact(new Date(mills).toISOString());
  }

  function millsToInputValue(mills: number): string {
    return formatDateForInput(new Date(mills).toISOString());
  }

  const bolusTypeOptions: BolusType[] = [
    "Normal",
    "Square",
    "Dual",
  ] as BolusType[];
  const glucoseTypeOptions: GlucoseType[] = [
    "Finger",
    "Sensor",
  ] as GlucoseType[];
  const deviceEventTypeOptions: DeviceEventType[] = [
    "SensorStart",
    "SensorChange",
    "SensorStop",
    "SiteChange",
    "InsulinChange",
    "PumpBatteryChange",
    "PodChange",
    "ReservoirChange",
    "CannulaChange",
    "TransmitterSensorInsert",
    "PodActivated",
    "PodDeactivated",
    "PumpSuspend",
    "PumpResume",
    "Priming",
    "TubePriming",
    "NeedlePriming",
    "Rewind",
    "DateChanged",
    "TimeChanged",
    "BolusMaxChanged",
    "BasalMaxChanged",
    "ProfileSwitch",
  ] as DeviceEventType[];
</script>

<Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
  <Dialog.Content class="max-w-lg max-h-[90vh] overflow-y-auto">
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
          Edit Record
        </Dialog.Title>
        <Dialog.Description>
          Edit the details of this {activeCategory.name.toLowerCase()} record.
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

        <!-- Date and Time -->
        <div class="space-y-2">
          <Label for="datetime">Date & Time</Label>
          <Input
            id="datetime"
            type="datetime-local"
            value={millsToInputValue(editMills)}
            onchange={(e) => {
              const val = e.currentTarget.value;
              if (val) editMills = new Date(val).getTime();
            }}
          />
        </div>

        <!-- Bolus form -->
        {#if activeRecord.kind === "bolus"}
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
                bind:value={bolusForm.insulin}
              />
            </div>
            <div class="space-y-2">
              <Label>Bolus Type</Label>
              <Select.Root
                type="single"
                value={bolusForm.bolusType ?? ""}
                onValueChange={(v) => {
                  bolusForm.bolusType = (v as BolusType) || undefined;
                }}
              >
                <Select.Trigger>
                  {bolusForm.bolusType || "Select..."}
                </Select.Trigger>
                <Select.Content>
                  {#each bolusTypeOptions as opt}
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
                bind:value={bolusForm.programmed}
                placeholder={"\u2014"}
              />
            </div>
            <div class="space-y-2">
              <Label for="delivered">Delivered</Label>
              <Input
                id="delivered"
                type="number"
                step="0.05"
                min="0"
                bind:value={bolusForm.delivered}
                placeholder={"\u2014"}
              />
            </div>
            <div class="space-y-2">
              <Label for="duration">Duration (min)</Label>
              <Input
                id="duration"
                type="number"
                step="1"
                min="0"
                bind:value={bolusForm.duration}
                placeholder={"\u2014"}
              />
            </div>
          </div>

          <div class="space-y-2">
            <Label for="insulinType">Insulin Type</Label>
            <Input
              id="insulinType"
              bind:value={bolusForm.insulinType}
              placeholder="e.g. Rapid, Long-acting"
            />
          </div>

          <div class="flex gap-6">
            <div class="flex items-center gap-2">
              <Checkbox id="automatic" bind:checked={bolusForm.automatic} />
              <Label for="automatic" class="text-sm font-normal cursor-pointer">
                Automatic
              </Label>
            </div>
            <div class="flex items-center gap-2">
              <Checkbox
                id="isBasalInsulin"
                bind:checked={bolusForm.isBasalInsulin}
              />
              <Label
                for="isBasalInsulin"
                class="text-sm font-normal cursor-pointer"
              >
                Basal Insulin
              </Label>
            </div>
          </div>

          <!-- Carbs form -->
        {:else if activeRecord.kind === "carbs"}
          <div class="grid grid-cols-3 gap-4">
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
                bind:value={carbsForm.carbs}
              />
            </div>
            <div class="space-y-2">
              <Label for="protein">Protein (g)</Label>
              <Input
                id="protein"
                type="number"
                step="1"
                min="0"
                bind:value={carbsForm.protein}
                placeholder={"\u2014"}
              />
            </div>
            <div class="space-y-2">
              <Label for="fat">Fat (g)</Label>
              <Input
                id="fat"
                type="number"
                step="1"
                min="0"
                bind:value={carbsForm.fat}
                placeholder={"\u2014"}
              />
            </div>
          </div>

          <div class="space-y-2">
            <Label for="foodType">Food Type</Label>
            <Input
              id="foodType"
              bind:value={carbsForm.foodType}
              placeholder="e.g. Lunch, Snack"
            />
          </div>

          <div class="grid grid-cols-2 gap-4">
            <div class="space-y-2">
              <Label for="absorptionTime">Absorption Time (min)</Label>
              <Input
                id="absorptionTime"
                type="number"
                step="1"
                min="0"
                bind:value={carbsForm.absorptionTime}
                placeholder={"\u2014"}
              />
            </div>
            <div class="space-y-2">
              <Label for="carbTime">Carb Time (min)</Label>
              <Input
                id="carbTime"
                type="number"
                step="1"
                bind:value={carbsForm.carbTime}
                placeholder={"\u2014"}
              />
            </div>
          </div>

          <!-- BG Check form -->
        {:else if activeRecord.kind === "bgCheck"}
          <div class="space-y-2">
            <Label for="glucose" class="flex items-center gap-1.5">
              <Droplet class="h-3.5 w-3.5 text-red-500" />
              Glucose
            </Label>
            <Input
              id="glucose"
              type="number"
              step="1"
              min="0"
              bind:value={bgCheckForm.glucose}
            />
          </div>

          <div class="grid grid-cols-2 gap-4">
            <div class="space-y-2">
              <Label>Glucose Type</Label>
              <Select.Root
                type="single"
                value={bgCheckForm.glucoseType ?? ""}
                onValueChange={(v) => {
                  bgCheckForm.glucoseType = (v as GlucoseType) || undefined;
                }}
              >
                <Select.Trigger>
                  {bgCheckForm.glucoseType || "Select..."}
                </Select.Trigger>
                <Select.Content>
                  {#each glucoseTypeOptions as opt}
                    <Select.Item value={opt}>{opt}</Select.Item>
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
            <div class="space-y-2">
              <Label>Units</Label>
              <Select.Root
                type="single"
                value={bgCheckForm.units ?? ""}
                onValueChange={(v) => {
                  bgCheckForm.units = (v as GlucoseUnit) || undefined;
                }}
              >
                <Select.Trigger>
                  {bgCheckForm.units === "MgDl"
                    ? "mg/dL"
                    : bgCheckForm.units === "Mmol"
                      ? "mmol/L"
                      : "Select..."}
                </Select.Trigger>
                <Select.Content>
                  <Select.Item value="MgDl">mg/dL</Select.Item>
                  <Select.Item value="Mmol">mmol/L</Select.Item>
                </Select.Content>
              </Select.Root>
            </div>
          </div>

          <!-- Note form -->
        {:else if activeRecord.kind === "note"}
          <div class="space-y-2">
            <Label for="text" class="flex items-center gap-1.5">
              <FileText class="h-3.5 w-3.5" />
              Text
            </Label>
            <Textarea
              id="text"
              bind:value={noteForm.text}
              placeholder="Note text..."
              rows={3}
            />
          </div>

          <div class="space-y-2">
            <Label for="eventType">Event Type</Label>
            <Input
              id="eventType"
              bind:value={noteForm.eventType}
              placeholder="e.g. Announcement, Note"
            />
          </div>

          <div class="flex items-center gap-2">
            <Checkbox
              id="isAnnouncement"
              bind:checked={noteForm.isAnnouncement}
            />
            <Label
              for="isAnnouncement"
              class="text-sm font-normal cursor-pointer"
            >
              Announcement
            </Label>
          </div>

          <!-- Device Event form -->
        {:else if activeRecord.kind === "deviceEvent"}
          <div class="space-y-2">
            <Label class="flex items-center gap-1.5">
              <Smartphone class="h-3.5 w-3.5 text-orange-500" />
              Event Type
            </Label>
            <Select.Root
              type="single"
              value={deviceEventForm.eventType ?? ""}
              onValueChange={(v) => {
                deviceEventForm.eventType = (v as DeviceEventType) || undefined;
              }}
            >
              <Select.Trigger>
                {deviceEventForm.eventType || "Select..."}
              </Select.Trigger>
              <Select.Content>
                {#each deviceEventTypeOptions as opt}
                  <Select.Item value={opt}>{opt}</Select.Item>
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="space-y-2">
            <Label for="deviceNotes">Notes</Label>
            <Textarea
              id="deviceNotes"
              bind:value={deviceEventForm.notes}
              placeholder="Additional notes..."
              rows={3}
            />
          </div>
        {/if}

        <!-- Linked Records -->
        {#if correlationGroup.length > 1}
          <Separator />
          <div class="space-y-3">
            <h4
              class="text-xs font-medium uppercase text-muted-foreground tracking-wide flex items-center gap-2"
            >
              <Link class="h-3.5 w-3.5" />
              Linked Records
              <Badge variant="secondary" class="text-xs h-5 px-1.5">
                {correlationGroup.length}
              </Badge>
            </h4>
            {#each correlationGroup as linked}
              {@const linkedStyle = getEntryStyle(linked.kind)}
              {@const linkedCategory = ENTRY_CATEGORIES[linked.kind]}
              {@const isActive = linked.data.id === activeRecord?.data.id}
              <button
                type="button"
                class="w-full text-left rounded-lg border p-3 transition-colors {isActive
                  ? 'border-primary bg-primary/5'
                  : 'hover:bg-muted/50'}"
                disabled={isActive}
                onclick={() => switchToRecord(linked)}
              >
                <div class="flex items-center gap-2">
                  <Badge
                    variant="outline"
                    class="{linkedStyle.colorClass} {linkedStyle.bgClass} {linkedStyle.borderClass} text-xs"
                  >
                    {linkedCategory.name}
                  </Badge>
                  <span class="text-sm">{getPrimaryValue(linked)}</span>
                  <span class="ml-auto text-xs text-muted-foreground">
                    {formatMills(linked.data.mills)}
                  </span>
                </div>
              </button>
            {/each}
          </div>
        {/if}

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
            {isLoading ? "Saving..." : "Save Changes"}
          </Button>
        </Dialog.Footer>
      </form>
    {:else}
      <Dialog.Header>
        <Dialog.Title>No Record Selected</Dialog.Title>
        <Dialog.Description>
          Click on a row in the table to view and edit its details.
        </Dialog.Description>
      </Dialog.Header>
    {/if}
  </Dialog.Content>
</Dialog.Root>
