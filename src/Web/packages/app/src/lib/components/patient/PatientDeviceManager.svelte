<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Badge } from "$lib/components/ui/badge";
  import * as Card from "$lib/components/ui/card";
  import * as Select from "$lib/components/ui/select";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as AlertDialog from "$lib/components/ui/alert-dialog";
  import { Textarea } from "$lib/components/ui/textarea";
  import {
    Cpu,
    Activity,
    Droplets,
    Syringe,
    PenLine,
    Upload,
    Plus,
    Pencil,
    Trash2,
    Save,
    Loader2,
  } from "lucide-svelte";
  import {
    type PatientDevice,
    DeviceCategory,
    AidAlgorithm,
  } from "$api";
  import {
    deviceCategoryLabels,
    aidAlgorithmLabels,
  } from "./labels";
  import { DeviceListState } from "./state.svelte";

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
    return d.toLocaleDateString(undefined, {
      year: "numeric",
      month: "short",
      day: "numeric",
    });
  }

  // ── Inline variant state ────────────────────────────────────────

  let showInlineForm = $state(false);
  let inlineCategory = $state("");
  let inlineManufacturer = $state("");
  let inlineModel = $state("");
  let inlineAidAlgorithm = $state("");

  let showInlineAid = $derived(inlineCategory === DeviceCategory.InsulinPump);
  let canAddInline = $derived(
    inlineCategory !== "" && inlineManufacturer.trim() !== "" && inlineModel.trim() !== "",
  );

  function resetInlineForm() {
    inlineCategory = "";
    inlineManufacturer = "";
    inlineModel = "";
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
  let deviceAidAlgorithm = $state<string>("");
  let deviceSerialNumber = $state("");
  let deviceStartDate = $state("");
  let deviceEndDate = $state("");
  let deviceIsCurrent = $state(true);
  let deviceNotes = $state("");

  let showAidAlgorithm = $derived(deviceCategory === DeviceCategory.InsulinPump);

  const activeForm = $derived(editing?.id ? deviceList.updateForm : deviceList.createForm);
  const dialogSaving = $derived(!!deviceList.createForm.pending || !!deviceList.updateForm.pending);

  function openDialog(device?: PatientDevice) {
    if (device) {
      editing = device;
      deviceCategory = device.deviceCategory ?? DeviceCategory.InsulinPump;
      deviceManufacturer = device.manufacturer ?? "";
      deviceModel = device.model ?? "";
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
</script>

{#if variant === "inline"}
  <!-- ── Inline variant (wizard-style) ─────────────────────────── -->

  {#if deviceList.items.length > 0}
    <div class="space-y-3">
      {#each deviceList.items as device}
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
                {[device.manufacturer, device.model].filter(Boolean).join(" ") || "Unknown Device"}
              </Card.Title>
              <Card.Description class="text-xs">
                {device.deviceCategory
                  ? (deviceCategoryLabels[device.deviceCategory] ?? device.deviceCategory)
                  : "Unknown Category"}
                {#if device.aidAlgorithm && device.aidAlgorithm !== AidAlgorithm.None}
                  &middot; {aidAlgorithmLabels[device.aidAlgorithm] ?? device.aidAlgorithm}
                {/if}
              </Card.Description>
            </div>
            {#if device.id}
              <Button
                variant="ghost"
                size="icon"
                class="shrink-0 text-muted-foreground hover:text-destructive"
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
        <Card.Title class="text-sm">Add a Device</Card.Title>
      </Card.Header>
      <form
        {...deviceList.createForm.enhance(async ({ submit }) => {
          await submit();
          if (deviceList.createForm.result) resetInlineForm();
        })}
      >
        <Card.Content class="@container space-y-4">
          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2">
              <Label for="device-category">Device Category</Label>
              <Select.Root type="single" name="deviceCategory" bind:value={inlineCategory}>
                <Select.Trigger id="device-category">
                  {inlineCategory
                    ? (deviceCategoryLabels[inlineCategory as DeviceCategory] ?? inlineCategory)
                    : "Select category"}
                </Select.Trigger>
                <Select.Content>
                  {#each Object.entries(deviceCategoryLabels) as [value, label]}
                    <Select.Item {value} {label} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>

            <div class="space-y-2">
              <Label for="manufacturer">Manufacturer</Label>
              <Input
                name="manufacturer"
                id="manufacturer"
                bind:value={inlineManufacturer}
                placeholder="e.g. Dexcom, Omnipod"
              />
            </div>

            <div class="space-y-2">
              <Label for="model">Model</Label>
              <Input
                name="model"
                id="model"
                bind:value={inlineModel}
                placeholder="e.g. G7, DASH"
              />
            </div>

            {#if showInlineAid}
              <div class="space-y-2">
                <Label for="aid-algorithm">AID Algorithm</Label>
                <Select.Root type="single" name="aidAlgorithm" bind:value={inlineAidAlgorithm}>
                  <Select.Trigger id="aid-algorithm">
                    {inlineAidAlgorithm
                      ? (aidAlgorithmLabels[inlineAidAlgorithm as AidAlgorithm] ?? inlineAidAlgorithm)
                      : "Select algorithm"}
                  </Select.Trigger>
                  <Select.Content>
                    {#each Object.entries(aidAlgorithmLabels) as [value, label]}
                      <Select.Item {value} {label} />
                    {/each}
                  </Select.Content>
                </Select.Root>
              </div>
            {/if}
          </div>
          <input type="hidden" name="b:isCurrent" value="on" />
          {#each deviceList.createForm.fields.allIssues() as issue}
            <p class="text-sm text-destructive">{issue.message}</p>
          {/each}
        </Card.Content>
        <Card.Footer class="flex justify-end gap-2">
          <Button type="button" variant="outline" onclick={resetInlineForm} disabled={!!deviceList.createForm.pending}>
            Cancel
          </Button>
          <Button type="submit" disabled={!canAddInline || !!deviceList.createForm.pending}>
            {deviceList.createForm.pending ? "Adding..." : "Add Device"}
          </Button>
        </Card.Footer>
      </form>
    </Card.Root>
  {:else}
    <Button variant="outline" onclick={() => (showInlineForm = true)}>
      <Plus class="h-4 w-4 mr-1" />
      Add Device
    </Button>
  {/if}

{:else}
  <!-- ── Dialog variant (settings-style) ───────────────────────── -->

  {#if deviceList.items.length === 0}
    <p class="text-sm text-muted-foreground py-4 text-center">
      No devices added yet. Add your first device to get started.
    </p>
  {:else}
    <div class="space-y-3">
      {#each deviceList.items as device}
        <div
          class="flex items-center justify-between rounded-lg border p-3"
        >
          <div class="space-y-1 min-w-0 flex-1">
            <div class="flex items-center gap-2 flex-wrap">
              <span class="font-medium text-sm">
                {device.manufacturer ?? "Unknown"} {device.model ?? ""}
              </span>
              <Badge variant="secondary" class="text-xs">
                {deviceCategoryLabels[(device.deviceCategory ?? "") as DeviceCategory] ??
                  device.deviceCategory}
              </Badge>
              {#if device.isCurrent}
                <Badge
                  variant="default"
                  class="text-xs bg-green-600 hover:bg-green-700"
                >
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
      Add Device
    </Button>
  </div>

  <!-- Device Dialog -->
  <Dialog.Root bind:open={dialogOpen}>
    <Dialog.Content class="sm:max-w-lg">
      <Dialog.Header>
        <Dialog.Title>
          {editing ? "Edit Device" : "Add Device"}
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
          <input type="hidden" name="id" value={editing.id} />
        {/if}
        <div class="@container space-y-4 py-4">
          <div class="space-y-2">
            <Label for="device-category">Category</Label>
            <Select.Root type="single" name="deviceCategory" bind:value={deviceCategory}>
              <Select.Trigger id="device-category">
                {deviceCategoryLabels[deviceCategory as DeviceCategory] ?? deviceCategory}
              </Select.Trigger>
              <Select.Content>
                {#each Object.entries(deviceCategoryLabels) as [value, label]}
                  <Select.Item {value} {label} />
                {/each}
              </Select.Content>
            </Select.Root>
          </div>

          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2">
              <Label for="device-manufacturer">Manufacturer</Label>
              <Input
                name="manufacturer"
                id="device-manufacturer"
                bind:value={deviceManufacturer}
                placeholder="e.g. Medtronic, Dexcom"
              />
            </div>
            <div class="space-y-2">
              <Label for="device-model">Model</Label>
              <Input
                name="model"
                id="device-model"
                bind:value={deviceModel}
                placeholder="e.g. 780G, G7"
              />
            </div>
          </div>

          {#if showAidAlgorithm}
            <div class="space-y-2">
              <Label for="device-aid">AID Algorithm</Label>
              <Select.Root type="single" name="aidAlgorithm" bind:value={deviceAidAlgorithm}>
                <Select.Trigger id="device-aid">
                  {deviceAidAlgorithm
                    ? (aidAlgorithmLabels[deviceAidAlgorithm as AidAlgorithm] ?? deviceAidAlgorithm)
                    : "Select algorithm"}
                </Select.Trigger>
                <Select.Content>
                  {#each Object.entries(aidAlgorithmLabels) as [value, label]}
                    <Select.Item {value} {label} />
                  {/each}
                </Select.Content>
              </Select.Root>
            </div>
          {/if}

          <div class="space-y-2">
            <Label for="device-serial">Serial Number</Label>
            <Input
              name="serialNumber"
              id="device-serial"
              bind:value={deviceSerialNumber}
              placeholder="Optional"
            />
          </div>

          <div class="grid gap-4 @sm:grid-cols-2">
            <div class="space-y-2">
              <Label for="device-start">Start Date</Label>
              <Input
                name="startDate"
                id="device-start"
                type="date"
                bind:value={deviceStartDate}
              />
            </div>
            <div class="space-y-2">
              <Label for="device-end">End Date</Label>
              <Input
                name="endDate"
                id="device-end"
                type="date"
                bind:value={deviceEndDate}
              />
            </div>
          </div>

          <div class="flex items-center gap-2">
            <input
              id="device-current"
              type="checkbox"
              name="isCurrent"
              bind:checked={deviceIsCurrent}
              class="h-4 w-4 rounded border-input"
            />
            <Label for="device-current">Currently in use</Label>
          </div>

          <div class="space-y-2">
            <Label for="device-notes">Notes</Label>
            <Textarea
              name="notes"
              id="device-notes"
              bind:value={deviceNotes}
              placeholder="Any additional notes about this device"
              rows={2}
            />
          </div>
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
  <AlertDialog.Root
    open={deleteId !== null}
    onOpenChange={(open) => {
      if (!open) deleteId = null;
    }}
  >
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title>Delete Device</AlertDialog.Title>
        <AlertDialog.Description>
          Are you sure you want to delete this device? This action cannot be
          undone.
        </AlertDialog.Description>
      </AlertDialog.Header>
      <AlertDialog.Footer>
        <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
        <AlertDialog.Action
          class="bg-destructive text-destructive-foreground hover:bg-destructive/90"
          onclick={handleDelete}
        >
          Delete
        </AlertDialog.Action>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>
{/if}
