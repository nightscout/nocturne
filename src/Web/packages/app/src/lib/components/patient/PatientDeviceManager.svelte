<script lang="ts">
  import { labelFor } from "#lib/components/ui/enum-value.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import Cpu from "@lucide/svelte/icons/cpu";
  import Activity from "@lucide/svelte/icons/activity";
  import Droplets from "@lucide/svelte/icons/droplets";
  import Syringe from "@lucide/svelte/icons/syringe";
  import PenLine from "@lucide/svelte/icons/pen-line";
  import Upload from "@lucide/svelte/icons/upload";
  import Plus from "@lucide/svelte/icons/plus";
  import Pencil from "@lucide/svelte/icons/pencil";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Save from "@lucide/svelte/icons/save";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import ChevronUp from "@lucide/svelte/icons/chevron-up";
  import ChevronDown from "@lucide/svelte/icons/chevron-down";
  import {
    type PatientDevice,
    type DiscoveredSource,
    type DeviceCatalogEntry,
    DeviceCategory,
    AidAlgorithm,
  } from "#lib/api/index.js";
  import {
    deviceCategoryLabels,
    aidAlgorithmLabels,
  } from "./labels";
  import { DeviceListState } from "./state.svelte";
  import { EmptyState } from "#lib/components/shared/index.js";
  import DeviceNameCombobox from "./DeviceNameCombobox.svelte";
  import { trackerNote } from "./device-catalog";

  interface Props {
    /** "inline" = wizard-style card forms, "dialog" = settings-style dialog CRUD */
    variant?: "inline" | "dialog";
  }

  let { variant = "dialog" }: Props = $props();

  const deviceList = new DeviceListState();

  // ── Category icons (for inline variant) ─────────────────────────

  const categoryIcons: Record<string, typeof Cpu> = {
    [DeviceCategory.InsulinPump]: Cpu,
    [DeviceCategory.CGM]: Activity,
    [DeviceCategory.GlucoseMeter]: Droplets,
    [DeviceCategory.InsulinPen]: Syringe,
    [DeviceCategory.SmartPen]: PenLine,
    [DeviceCategory.Uploader]: Upload,
  };

  // ── Helpers ──────────────────────────────────────────────────────

  function formatDate(date: Date | string | undefined): string {
    if (!date) return "";
    const d = new Date(date);
    return formatMediumDate(d);
  }

  // ── Inline variant state ────────────────────────────────────────

  let showInlineForm = $state(false);
  let inlineCategory = $state("");
  let inlineManufacturer = $state("");
  let inlineModel = $state("");
  let inlineCatalogId = $state("");
  let inlineAidAlgorithm = $state("");

  const inlineEntry = $derived(deviceList.catalog.find((e) => e.id === inlineCatalogId));

  function pickInline(entry: DeviceCatalogEntry) {
    inlineCatalogId = entry.id ?? "";
    inlineCategory = entry.category ?? inlineCategory;
    inlineManufacturer = entry.manufacturer ?? "";
    inlineModel = entry.name ?? "";
  }

  function nameInline(name: string) {
    inlineCatalogId = "";
    inlineModel = name;
  }

  let showInlineAid = $derived(inlineCategory === DeviceCategory.InsulinPump);
  let canAddInline = $derived(
    inlineCategory !== "" && inlineManufacturer.trim() !== "" && inlineModel.trim() !== "",
  );

  function resetInlineForm() {
    inlineCategory = "";
    inlineManufacturer = "";
    inlineModel = "";
    inlineCatalogId = "";
    inlineAidAlgorithm = "";
    showInlineForm = false;
  }

  // ── Dialog variant state ────────────────────────────────────────

  let dialogOpen = $state(false);
  let editing = $state<PatientDevice | null>(null);
  let deleteId = $state<string | null>(null);

  let deviceCategory = $state<string>(DeviceCategory.InsulinPump);
  let deviceManufacturer = $state("");
  let deviceModel = $state("");
  let deviceCatalogId = $state("");
  let deviceAidAlgorithm = $state<string>("");
  let deviceSerialNumber = $state("");
  let deviceStartDate = $state("");
  let deviceEndDate = $state("");
  let deviceIsCurrent = $state(true);
  let deviceNotes = $state("");

  let showAidAlgorithm = $derived(deviceCategory === DeviceCategory.InsulinPump);

  const dialogEntry = $derived(deviceList.catalog.find((e) => e.id === deviceCatalogId));

  function pickDialog(entry: DeviceCatalogEntry) {
    deviceCatalogId = entry.id ?? "";
    deviceCategory = entry.category ?? deviceCategory;
    deviceManufacturer = entry.manufacturer ?? "";
    deviceModel = entry.name ?? "";
  }

  function nameDialog(name: string) {
    deviceCatalogId = "";
    deviceModel = name;
  }

  const activeForm = $derived(editing?.id ? deviceList.updateForm : deviceList.createForm);
  const dialogSaving = $derived(!!deviceList.createForm.pending || !!deviceList.updateForm.pending);
  // updateDevice's schema is { id, request: PatientDeviceSchema } — fields must nest under "request."
  // for SvelteKit's dot-path form parsing; createDevice's schema is flat.
  // updateDevice takes the record's fields under `request`; createDevice takes them flat.
  const deviceFields = $derived(
    editing?.id ? deviceList.updateForm.fields.request : deviceList.createForm.fields,
  );

  function openDialog(device?: PatientDevice) {
    if (device) {
      editing = device;
      deviceCategory = device.deviceCategory ?? DeviceCategory.InsulinPump;
      deviceManufacturer = device.manufacturer ?? "";
      deviceModel = device.model ?? "";
      deviceCatalogId = device.catalogId ?? "";
      deviceAidAlgorithm = device.aidAlgorithm ?? "";
      deviceSerialNumber = device.serialNumber ?? "";
      deviceStartDate = device.startDate
        ? new Date(device.startDate).toISOString().split("T")[0]
        : "";
      deviceEndDate = device.endDate
        ? new Date(device.endDate).toISOString().split("T")[0]
        : "";
      deviceIsCurrent = device.isCurrent ?? true;
      deviceNotes = device.notes ?? "";
    } else {
      editing = null;
      deviceCategory = DeviceCategory.InsulinPump;
      deviceManufacturer = "";
      deviceModel = "";
      deviceCatalogId = "";
      deviceAidAlgorithm = "";
      deviceSerialNumber = "";
      deviceStartDate = "";
      deviceEndDate = "";
      deviceIsCurrent = true;
      deviceNotes = "";
    }
    dialogOpen = true;
  }

  async function handleDelete() {
    if (!deleteId) return;
    await deviceList.remove(deleteId);
    deleteId = null;
  }

  // ── Rank reordering ─────────────────────────────────────────────

  /** Moves the device at {@link index} one slot toward the front (-1) or back (+1) and persists the order. */
  async function moveDevice(index: number, direction: -1 | 1) {
    const ids = deviceList.items.map((d) => d.id).filter((id): id is string => !!id);
    const target = index + direction;
    if (target < 0 || target >= ids.length) return;
    [ids[index], ids[target]] = [ids[target], ids[index]];
    await deviceList.reorder(ids);
  }

  // ── Discovered sources ──────────────────────────────────────────

  function formatLastSeen(value: string | Date | null | undefined): string {
    if (!value) return "";
    return formatMediumDate(value);
  }

  /**
   * Opens the device dialog pre-filled from a discovered source as a CGM. The user sets the usage
   * dates before saving; registration back-stamps the source's readings within that window.
   */
  function registerDiscovered(source: DiscoveredSource) {
    openDialog();
    deviceCategory = DeviceCategory.CGM;
    deviceModel = source.device ?? source.dataSource ?? "";
  }
</script>

{#if variant === "inline"}
  <!-- ── Inline variant (wizard-style) ─────────────────────────── -->

  {#if deviceList.items.length > 0}
    <div class="space-y-3">
      {#each deviceList.items as device (device.id)}
        <Card.Root>
          <Card.Header class="flex flex-row items-center gap-3 py-3">
            <div class="flex h-9 w-9 shrink-0 items-center justify-center rounded-md bg-muted">
              {#if device.deviceCategory && categoryIcons[device.deviceCategory]}
                {@const Icon = categoryIcons[device.deviceCategory]}
                <Icon class="h-4 w-4" />
              {:else}
                <Cpu class="h-4 w-4" />
              {/if}
            </div>
            <div class="flex-1 min-w-0">
              <Card.Title class="text-sm font-medium">
                {[device.manufacturer, device.model].filter(Boolean).join(" ") || "Unknown device"}
              </Card.Title>
              <Card.Description size="sm">
                {device.deviceCategory
                  ? (deviceCategoryLabels[device.deviceCategory] ?? device.deviceCategory)
                  : "Unknown category"}
                {#if device.aidAlgorithm && device.aidAlgorithm !== AidAlgorithm.None}
                  &middot; {aidAlgorithmLabels[device.aidAlgorithm] ?? device.aidAlgorithm}
                {/if}
              </Card.Description>
            </div>
            {#if device.id}
              <Button
                variant="ghost-destructive"
                size="icon"
                class="shrink-0"
                onclick={() => deviceList.remove(device.id!)}
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            {/if}
          </Card.Header>
        </Card.Root>
      {/each}
    </div>
  {/if}

  {#if showInlineForm}
    <Card.Root>
      <Card.Header>
        <Card.Title class="text-sm">Add a device</Card.Title>
      </Card.Header>
      <form
        {...deviceList.createForm.enhance(async ({ submit }) => {
          await submit();
          if (deviceList.createForm.result) resetInlineForm();
        })}
      >
        <Card.Content class="@container space-y-4">
          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2 @sm:col-span-2">
              <Label for="device-name-inline">Device</Label>
              <DeviceNameCombobox
                id="device-name-inline"
                catalog={deviceList.catalog}
                value={inlineModel}
                catalogId={inlineCatalogId}
                onPick={pickInline}
                onCustom={nameInline}
              />
              <input {...deviceList.createForm.fields.model.as("hidden", inlineModel)} />
              {#if inlineCatalogId}
                <input {...deviceList.createForm.fields.catalogId.as("hidden", inlineCatalogId)} />
              {/if}
              {#if trackerNote(inlineEntry)}
                <p class="text-xs text-muted-foreground">{trackerNote(inlineEntry)}</p>
              {/if}
            </div>

            <div class="space-y-2">
              <Label for="device-category">Device category</Label>
              <Select.Root
                type="single"
                name={deviceList.createForm.fields.deviceCategory.as("select").name}
                bind:value={inlineCategory}
                onValueChange={(v: string) => {
                  if (inlineEntry && inlineEntry.category !== v) inlineCatalogId = "";
                }}
              >
                <Select.Trigger id="device-category">
                  {inlineCategory
                    ? (labelFor(deviceCategoryLabels, inlineCategory) ?? inlineCategory)
                    : "Select category"}
                </Select.Trigger>
                <Select.Content>
                  {#each Object.entries(deviceCategoryLabels) as [value, label] (value)}
                    <Select.Item {value} {label} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>

            <div class="space-y-2">
              <Label for="manufacturer">Manufacturer</Label>
              <Input
                name={deviceList.createForm.fields.manufacturer.as("text").name}
                id="manufacturer"
                bind:value={inlineManufacturer}
                placeholder="e.g. Dexcom, Insulet"
              />
            </div>

            {#if showInlineAid}
              <div class="space-y-2">
                <Label for="aid-algorithm">AID algorithm</Label>
                <Select.Root type="single" name={deviceList.createForm.fields.aidAlgorithm.as("select").name} bind:value={inlineAidAlgorithm}>
                  <Select.Trigger id="aid-algorithm">
                    {inlineAidAlgorithm
                      ? (labelFor(aidAlgorithmLabels, inlineAidAlgorithm) ?? inlineAidAlgorithm)
                      : "Select algorithm"}
                  </Select.Trigger>
                  <Select.Content>
                    {#each Object.entries(aidAlgorithmLabels) as [value, label] (value)}
                      <Select.Item {value} {label} />
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>
            {/if}
          </div>
          <input {...deviceList.createForm.fields.isCurrent.as("hidden", true)} />
          {#each deviceList.createForm.fields.allIssues() ?? [] as issue, i (i)}
            <p class="text-sm text-destructive">{issue.message}</p>
          {/each}
        </Card.Content>
        <Card.Footer class="flex justify-end gap-2">
          <Button type="button" variant="outline" onclick={resetInlineForm} disabled={!!deviceList.createForm.pending}>
            Cancel
          </Button>
          <Button type="submit" disabled={!canAddInline || !!deviceList.createForm.pending}>
            {deviceList.createForm.pending ? "Adding..." : "Add device"}
          </Button>
        </Card.Footer>
      </form>
    </Card.Root>
  {:else}
    <Button variant="outline" onclick={() => (showInlineForm = true)}>
      <Plus class="h-4 w-4 mr-1" />
      Add device
    </Button>
  {/if}

{:else}
  <!-- ── Dialog variant (settings-style) ───────────────────────── -->

  {#if deviceList.items.length === 0}
    <EmptyState
      art="cgm-sensor"
      variant="outline"
      title="No devices added yet"
      body="Add your first device to get started."
    />
  {:else}
    <div class="space-y-3">
      {#each deviceList.items as device, i (device.id)}
        <div
          class="flex items-center justify-between rounded-lg border p-3"
        >
          <div class="space-y-1 min-w-0 flex-1">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="font-medium text-sm">
                {device.manufacturer ?? "Unknown"} {device.model ?? ""}
              </span>
              <Badge variant="secondary">
                {labelFor(deviceCategoryLabels, device.deviceCategory) ??
                  device.deviceCategory}
              </Badge>
              {#if device.isCurrent}
                <Badge variant="success">
                  Current
                </Badge>
              {/if}
            </div>
            <div class="flex items-center gap-3 text-xs text-muted-foreground flex-wrap">
              {#if device.aidAlgorithm && device.aidAlgorithm !== AidAlgorithm.None}
                <span>
                  AID: {aidAlgorithmLabels[device.aidAlgorithm] ??
                    device.aidAlgorithm}
                </span>
              {/if}
              {#if device.startDate}
                <span>From {formatDate(device.startDate)}</span>
              {/if}
              {#if device.endDate}
                <span>Until {formatDate(device.endDate)}</span>
              {/if}
              {#if device.serialNumber}
                <span class="font-mono">SN: {device.serialNumber}</span>
              {/if}
            </div>
          </div>
          <div class="flex items-center gap-1 ml-2">
            {#if deviceList.items.length > 1}
              <div class="flex flex-col">
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Move device up in priority"
                  disabled={i === 0}
                  onclick={() => moveDevice(i, -1)}
                >
                  <ChevronUp class="h-4 w-4" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon-xs"
                  aria-label="Move device down in priority"
                  disabled={i === deviceList.items.length - 1}
                  onclick={() => moveDevice(i, 1)}
                >
                  <ChevronDown class="h-4 w-4" />
                </Button>
              </div>
            {/if}
            <Button
              variant="ghost"
              size="icon"
              onclick={() => openDialog(device)}
            >
              <Pencil class="h-4 w-4" />
            </Button>
            <Button
              variant="ghost"
              size="icon"
              onclick={() => (deleteId = device.id ?? null)}
            >
              <Trash2 class="h-4 w-4 text-destructive" />
            </Button>
          </div>
        </div>
      {/each}
    </div>
  {/if}

  <!-- Add button for dialog variant -->
  <div class="pt-2">
    <Button size="sm" onclick={() => openDialog()}>
      <Plus class="mr-2 h-4 w-4" />
      Add device
    </Button>
  </div>

  <!-- Discovered sources: unattributed streams seen recently -->
  {#if deviceList.discoveredSources.length > 0}
    <div class="pt-4 space-y-2">
      <div>
        <h4 class="text-sm font-medium">Discovered sources</h4>
        <p class="text-xs text-muted-foreground">
          Streams seen in recent readings that are not linked to a device yet. Register one to
          attribute its readings.
        </p>
      </div>
      <div class="space-y-2">
        {#each deviceList.discoveredSources as source (JSON.stringify([source.dataSource, source.device]))}
          <div class="flex items-center justify-between rounded-lg border border-dashed p-3">
            <div class="space-y-1 min-w-0 flex-1">
              <div class="flex items-center gap-2 flex-wrap">
                <span class="font-medium text-sm">
                  {source.device || source.dataSource || "Unknown source"}
                </span>
                {#if source.device && source.dataSource}
                  <Badge variant="outline" class="font-mono">{source.dataSource}</Badge>
                {/if}
              </div>
              <div class="flex items-center gap-3 text-xs text-muted-foreground flex-wrap">
                <span>{source.readingCount} reading{source.readingCount === 1 ? "" : "s"}</span>
                <span>Last seen {formatLastSeen(source.lastSeen)}</span>
              </div>
            </div>
            <Button
              variant="outline"
              size="sm"
              class="ml-2 shrink-0"
              onclick={() => registerDiscovered(source)}
            >
              Register as device
            </Button>
          </div>
        {/each}
      </div>
    </div>
  {/if}

  <!-- Device Dialog -->
  <Dialog.Root bind:open={dialogOpen}>
    <Dialog.Content class="sm:max-w-lg">
      <Dialog.Header>
        <Dialog.Title>
          {editing ? "Edit device" : "Add device"}
        </Dialog.Title>
        <Dialog.Description>
          {editing
            ? "Update the details of this device."
            : "Add a new device to your patient record."}
        </Dialog.Description>
      </Dialog.Header>

      <form
        {...activeForm.enhance(async ({ submit }) => {
          await submit();
          if (activeForm.result) dialogOpen = false;
        })}
      >
        {#if editing?.id}
          <input {...deviceList.updateForm.fields.id.as("hidden", editing.id)} />
        {/if}
        <div class="@container space-y-4 py-4">
          <div class="space-y-2">
            <Label for="device-name">Device</Label>
            <DeviceNameCombobox
              id="device-name"
              catalog={deviceList.catalog}
              value={deviceModel}
              catalogId={deviceCatalogId}
              onPick={pickDialog}
              onCustom={nameDialog}
            />
            <input {...deviceFields.model.as("hidden", deviceModel)} />
            {#if deviceCatalogId}
              <input {...deviceFields.catalogId.as("hidden", deviceCatalogId)} />
            {/if}
            {#if trackerNote(dialogEntry)}
              <p class="text-xs text-muted-foreground">{trackerNote(dialogEntry)}</p>
            {/if}
          </div>

          <div class="space-y-2">
            <Label for="device-category">Category</Label>
            <Select.Root
              type="single"
              name={deviceFields.deviceCategory.as("select").name}
              bind:value={deviceCategory}
              onValueChange={(v: string) => {
                if (dialogEntry && dialogEntry.category !== v) deviceCatalogId = "";
              }}
            >
              <Select.Trigger id="device-category">
                {labelFor(deviceCategoryLabels, deviceCategory) ?? deviceCategory}
              </Select.Trigger>
              <Select.Content>
                {#each Object.entries(deviceCategoryLabels) as [value, label] (value)}
                  <Select.Item {value} {label} />
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="space-y-2">
            <Label for="device-manufacturer">Manufacturer</Label>
            <Input
              name={deviceFields.manufacturer.as("text").name}
              id="device-manufacturer"
              bind:value={deviceManufacturer}
              placeholder="e.g. Medtronic, Dexcom"
            />
          </div>

          {#if showAidAlgorithm}
            <div class="space-y-2">
              <Label for="device-aid">AID algorithm</Label>
              <Select.Root type="single" name={deviceFields.aidAlgorithm.as("select").name} bind:value={deviceAidAlgorithm}>
                <Select.Trigger id="device-aid">
                  {deviceAidAlgorithm
                    ? (labelFor(aidAlgorithmLabels, deviceAidAlgorithm) ?? deviceAidAlgorithm)
                    : "Select algorithm"}
                </Select.Trigger>
                <Select.Content>
                  {#each Object.entries(aidAlgorithmLabels) as [value, label] (value)}
                    <Select.Item {value} {label} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
          {/if}

          <div class="space-y-2">
            <Label for="device-serial">Serial number</Label>
            <Input
              name={deviceFields.serialNumber.as("text").name}
              id="device-serial"
              bind:value={deviceSerialNumber}
              placeholder="Optional"
            />
          </div>

          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2">
              <Label for="device-start">Start date</Label>
              <Input
                name={deviceFields.startDate.as("date").name}
                id="device-start"
                type="date"
                bind:value={deviceStartDate}
              />
            </div>
            <div class="space-y-2">
              <Label for="device-end">End date</Label>
              <Input
                name={deviceFields.endDate.as("date").name}
                id="device-end"
                type="date"
                bind:value={deviceEndDate}
              />
            </div>
          </div>

          <div class="flex items-center gap-2">
            <Checkbox
              id="device-current"
              name={deviceFields.isCurrent.as("checkbox").name}
              bind:checked={deviceIsCurrent}
            />
            <Label for="device-current">Currently in use</Label>
          </div>

          <div class="space-y-2">
            <Label for="device-notes">Notes</Label>
            <Textarea
              name={deviceFields.notes.as("text").name}
              id="device-notes"
              bind:value={deviceNotes}
              placeholder="Any additional notes about this device"
              rows={2}
            />
          </div>

          {#each activeForm.fields.allIssues() ?? [] as issue, i (i)}
            <p class="text-sm text-destructive">{issue.message}</p>
          {/each}
        </div>

        <Dialog.Footer>
          <Button
            type="button"
            variant="outline"
            onclick={() => (dialogOpen = false)}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={dialogSaving}>
            {#if dialogSaving}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {:else}
              <Save class="mr-2 h-4 w-4" />
            {/if}
            {editing ? "Update" : "Add"} Device
          </Button>
        </Dialog.Footer>
      </form>
    </Dialog.Content>
  </Dialog.Root>

  <!-- Device Delete Confirmation -->
  <ConfirmDialog
    open={deleteId !== null}
    onOpenChange={(open) => {
      if (!open) deleteId = null;
    }}
    title="Delete device"
    confirmLabel="Delete"
    destructive
    onConfirm={handleDelete}
  >
    {#snippet description()}
      Are you sure you want to delete this device? This action cannot be
      undone.
    {/snippet}
  </ConfirmDialog>
{/if}
