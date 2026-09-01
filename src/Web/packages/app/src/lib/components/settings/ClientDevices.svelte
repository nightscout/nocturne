<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { ConfirmDialog } from "$lib/components/ui/confirm-dialog";
  import { Badge } from "$lib/components/ui/badge";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Smartphone,
    Trash2,
    Check,
    X,
    AlertTriangle,
    Clock,
    LoaderCircle,
    Pencil,
  } from "lucide-svelte";
  import { timeAgo } from "$lib/utils";
  import { Now } from "$lib/hooks/now.svelte";

  const now = new Now();
  import { deviceKindLabel } from "$lib/utils/device-kind-labels";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import {
    getDevices,
    getCapabilityCatalog,
    rename,
    revoke,
  } from "$lib/api/generated/clientDevices.generated.remote";

  const devicesQuery = getDevices();
  const catalogQuery = getCapabilityCatalog();
  const devices = $derived(devicesQuery.current ?? []);

  // Map capability key -> human label from the catalog.
  const capabilityLabels = $derived.by(() => {
    const map = new Map<string, string>();
    for (const cap of catalogQuery.current?.capabilities ?? []) {
      if (cap.key) map.set(cap.key, cap.label ?? cap.key);
    }
    return map;
  });

  // Inline rename state.
  let editingId = $state<string | null>(null);
  let editLabel = $state("");
  let isSaving = $state<string | null>(null);
  let isRevoking = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

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
      successMessage = "Device renamed.";
      editingId = null;
      clearMessages();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Failed to rename device. Please try again.");
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
      successMessage = "Device revoked.";
      clearMessages();
    } catch (err) {
      errorMessage = describeSubmitError(err, "Failed to revoke device. Please try again.");
      clearMessages();
    } finally {
      isRevoking = null;
    }
  }
</script>

<div class="space-y-4">
  <div class="space-y-1">
    <h2 class="text-lg font-semibold tracking-tight">Devices</h2>
    <p class="text-sm text-muted-foreground">
      Apps registered to receive alerts and actuations, such as the desktop
      Companion
    </p>
  </div>

  {#if errorMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
    >
      <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
      <p class="text-sm text-destructive">{errorMessage}</p>
    </div>
  {/if}

  {#if successMessage}
    <div
      class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
    >
      <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400" />
      <p class="text-sm text-green-800 dark:text-green-200">{successMessage}</p>
    </div>
  {/if}

  {#if devices.length === 0}
    <Card.Root>
      <Card.Content
        class="flex flex-col items-center justify-center py-12 text-center"
      >
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <Smartphone class="h-6 w-6 text-muted-foreground" />
        </div>
        <p class="text-sm text-muted-foreground max-w-sm">
          No registered devices. When you pair an app such as the Companion, it
          will appear here.
        </p>
      </Card.Content>
    </Card.Root>
  {:else}
    {#each devices as device (device.id)}
      <Card.Root>
        <Card.Header>
          <div class="flex items-start justify-between gap-4">
            <div class="space-y-1 flex-1 min-w-0">
              <Card.Title class="flex items-center gap-2 flex-wrap">
                {#if editingId === device.id}
                  <Input
                    bind:value={editLabel}
                    placeholder="Device name"
                    maxlength={255}
                    class="h-8 max-w-xs"
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
              </Card.Title>
            </div>

            <div class="flex items-center gap-2 shrink-0">
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
                      variant="outline"
                      size="sm"
                      class="text-destructive border-destructive/30 hover:bg-destructive/10"
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
        </Card.Header>
        <Card.Content class="space-y-4">
          {#if (device.capabilities ?? []).length > 0}
            <div>
              <p
                class="mb-2 text-xs font-medium text-muted-foreground uppercase tracking-wider"
              >
                Capabilities
              </p>
              <ul class="space-y-1.5">
                {#each device.capabilities ?? [] as cap}
                  <li class="flex items-start gap-2 text-sm">
                    <Check class="mt-0.5 h-3.5 w-3.5 shrink-0 text-primary" />
                    <span class="text-muted-foreground">
                      {capabilityLabels.get(cap) ?? cap}
                    </span>
                  </li>
                {/each}
              </ul>
            </div>

            <Separator />
          {/if}

          <div
            class="flex flex-wrap gap-x-6 gap-y-1 text-xs text-muted-foreground"
          >
            {#if device.lastSeenAt}
              <span class="flex items-center gap-1.5">
                <Clock class="h-3 w-3" />
                Last seen {timeAgo(new Date(device.lastSeenAt).getTime(), undefined, now.current)}
              </span>
            {/if}
          </div>
        </Card.Content>
      </Card.Root>
    {/each}
  {/if}
</div>
