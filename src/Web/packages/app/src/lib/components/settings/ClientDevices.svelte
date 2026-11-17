<script lang="ts">
  import SuccessBanner from "#lib/forms/SuccessBanner.svelte";
  import { indexBy } from "#lib/utils/collections.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import Check from "@lucide/svelte/icons/check";
  import X from "@lucide/svelte/icons/x";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import Pencil from "@lucide/svelte/icons/pencil";
  import ConnectionTime from "#lib/components/connectors/ConnectionTime.svelte";
  import { Now } from "#lib/hooks/now.svelte.js";

  const now = new Now();
  import { deviceKindLabel } from "#lib/utils/device-kind-labels.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import {
    getDevices,
    getCapabilityCatalog,
    rename,
    revoke,
  } from "#lib/api/generated/clientDevices.generated.remote.js";

  const devicesQuery = getDevices();
  const catalogQuery = getCapabilityCatalog();
  const devices = $derived(devicesQuery.current ?? []);

  // Map capability key -> human label from the catalog.
  const capabilityLabels = $derived(
    indexBy(
      catalogQuery.current?.capabilities ?? [],
      (cap) => cap.key || null,
      (cap) => cap.label ?? cap.key ?? ""
    )
  );

  // Inline rename state.
  let editingId = $state<string | null>(null);
  let editLabel = $state("");
  let isSaving = $state<string | null>(null);
  let isRevoking = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<{ text: string; wash: boolean } | null>(null);

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  function startEdit(id: string, currentLabel: string | undefined) {
    editingId = id;
    editLabel = currentLabel ?? "";
    errorMessage = null;
  }

  function cancelEdit() {
    editingId = null;
    editLabel = "";
  }

  async function saveRename(id: string) {
    isSaving = id;
    errorMessage = null;
    try {
      await rename({ id, request: { label: editLabel.trim() || null } });
      successMessage = { text: "Device renamed.", wash: true };
      editingId = null;
      clearMessages();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Failed to rename device. Try again.");
      clearMessages();
    } finally {
      isSaving = null;
    }
  }

  async function handleRevoke(id: string) {
    isRevoking = id;
    errorMessage = null;
    try {
      await revoke(id);
      successMessage = { text: "Device revoked.", wash: false };
      clearMessages();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Failed to revoke device. Try again.");
      clearMessages();
    } finally {
      isRevoking = null;
    }
  }
</script>

<Card.Root>
  <Card.Header>
    <h2 class="font-semibold leading-none">Devices</h2>
    <Card.Description>
      Apps registered to receive alerts and actuations, such as the desktop
      Companion
    </Card.Description>
  </Card.Header>
  <Card.Content class="space-y-4">
    {#if errorMessage}
      <div
        class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
      >
        <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {/if}

    {#if successMessage}
      <SuccessBanner wash={successMessage.wash}>{successMessage.text}</SuccessBanner>
    {/if}

    {#if devices.length === 0}
      <p class="text-sm text-muted-foreground">
        No registered devices. A paired app, such as the Companion, shows here.
      </p>
    {:else}
      <ul class="divide-y border-y">
        {#each devices as device (device.id)}
          <li class="space-y-3 py-4">
            <div class="flex flex-wrap items-start justify-between gap-3">
              <div class="flex min-w-0 flex-1 flex-wrap items-center gap-2 font-medium">
                {#if editingId === device.id}
                  <Input
                    bind:value={editLabel}
                    placeholder="Device name"
                    maxlength={255}
                    size="sm"
                    class="max-w-xs"
                    disabled={isSaving === device.id}
                    aria-label="Device name"
                  />
                {:else}
                  <span class="truncate">
                    {device.label || deviceKindLabel(device.kind)}
                  </span>
                {/if}
                <Badge variant="secondary" class="shrink-0">
                  {deviceKindLabel(device.kind)}
                </Badge>
              </div>

              <div class="flex shrink-0 items-center gap-2">
                {#if editingId === device.id}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onclick={() => saveRename(device.id!)}
                    disabled={isSaving === device.id}
                  >
                    {#if isSaving === device.id}
                      <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                    {:else}
                      <Check class="mr-1.5 h-3.5 w-3.5" />
                    {/if}
                    Save
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="sm"
                    onclick={cancelEdit}
                    disabled={isSaving === device.id}
                  >
                    <X class="mr-1.5 h-3.5 w-3.5" />
                    Cancel
                  </Button>
                {:else}
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onclick={() => startEdit(device.id!, device.label)}
                  >
                    <Pencil class="mr-1.5 h-3.5 w-3.5" />
                    Rename
                  </Button>
                  <ConfirmDialog
                    title="Revoke device"
                    confirmLabel="Revoke"
                    onConfirm={() => handleRevoke(device.id!)}
                  >
                    {#snippet trigger(props)}
                      <Button
                        {...props}
                        type="button"
                        variant="outline-destructive"
                        size="sm"
                        disabled={isRevoking === device.id}
                      >
                        {#if isRevoking === device.id}
                          <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                        {:else}
                          <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                        {/if}
                        Revoke
                      </Button>
                    {/snippet}
                    {#snippet description()}
                      Revoke this device? It will stop receiving alerts until
                      it re-registers.
                    {/snippet}
                  </ConfirmDialog>
                {/if}
              </div>
            </div>

            {#if (device.capabilities ?? []).length > 0}
              <div>
                <p class="mb-2 text-sm font-medium">Capabilities</p>
                <ul class="space-y-1.5">
                  {#each device.capabilities ?? [] as cap, i (i)}
                    <li class="flex items-start gap-2 text-sm">
                      <Check
                        class="mt-0.5 h-3.5 w-3.5 shrink-0 text-muted-foreground"
                        aria-hidden="true"
                      />
                      <span class="text-muted-foreground">
                        {capabilityLabels.get(cap) ?? cap}
                      </span>
                    </li>
                  {/each}
                </ul>
              </div>
            {/if}

            <p class="flex flex-wrap gap-x-4 gap-y-0.5 text-sm text-muted-foreground">
              <ConnectionTime
                label="Last seen"
                at={device.lastSeenAt}
                now={now.current}
              />
              <span>
                {#if device.appName}
                  Paired through {device.appName}
                {:else if device.linkedToApp}
                  Paired through an app that reports no name.
                {:else}
                  Not linked to an app. Revoking an app will not remove this device.
                {/if}
              </span>
            </p>
          </li>
        {/each}
      </ul>
    {/if}
  </Card.Content>
</Card.Root>
