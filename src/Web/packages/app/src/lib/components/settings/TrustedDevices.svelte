<script lang="ts">
  import SuccessBanner from "#lib/forms/SuccessBanner.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { ConfirmDialog } from "#lib/components/ui/confirm-dialog/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import MonitorSmartphone from "@lucide/svelte/icons/monitor-smartphone";
  import Trash2 from "@lucide/svelte/icons/trash-2";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Clock from "@lucide/svelte/icons/clock";
  import LoaderCircle from "@lucide/svelte/icons/loader-circle";
  import { EmptyState } from "#lib/components/shared/index.js";
  import { formatMediumDate } from "#lib/utils/formatting.js";
  import {
    list,
    revoke,
    revokeAll,
  } from "#lib/api/generated/trustedDevices.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";

  const devicesQuery = list();
  const devices = $derived(devicesQuery.current ?? []);

  let isRemoving = $state<string | null>(null);
  let isRemovingAll = $state(false);
  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }

  async function handleRemove(id: string) {
    isRemoving = id;
    errorMessage = null;
    try {
      await revoke(id);
      successMessage = "Device removed.";
    } catch (err) {
      errorMessage = describeSubmitError(
        err,
        "Could not remove the device. Try again."
      );
    } finally {
      isRemoving = null;
      clearMessages();
    }
  }

  async function handleRemoveAll() {
    isRemovingAll = true;
    errorMessage = null;
    try {
      await revokeAll();
      successMessage = "All trusted devices removed.";
    } catch (err) {
      errorMessage = describeSubmitError(
        err,
        "Could not remove the trusted devices. Try again."
      );
    } finally {
      isRemovingAll = false;
      clearMessages();
    }
  }
</script>

<div class="space-y-4">
  <div class="flex flex-wrap items-start justify-between gap-3">
    <div class="space-y-1">
      <h2 class="text-lg font-semibold tracking-tight">Trusted devices</h2>
      <p class="text-sm text-muted-foreground">
        Devices that sign in without an authenticator code
      </p>
    </div>
    {#if devices.length > 1}
      <ConfirmDialog
        title="Remove all trusted devices"
        confirmLabel="Remove all"
        onConfirm={handleRemoveAll}
      >
        {#snippet trigger(props)}
          <Button
            {...props}
            type="button"
            variant="outline"
            size="sm"
            class="shrink-0"
            disabled={isRemovingAll}
          >
            {#if isRemovingAll}
              <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
            {:else}
              <Trash2 class="mr-1.5 h-3.5 w-3.5" />
            {/if}
            Remove all
          </Button>
        {/snippet}
        {#snippet description()}
          The next sign-in on each device will need an authenticator code.
          The devices stay signed in.
        {/snippet}
      </ConfirmDialog>
    {/if}
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
    <SuccessBanner wash={false}>{successMessage}</SuccessBanner>
  {/if}

  {#if devices.length === 0}
    <EmptyState
      art="phone"
      variant="card"
      title="No trusted devices"
      body="To trust a device, select “Trust this device” when you enter your authenticator code."
    />
  {:else}
    {#each devices as device (device.id)}
      <Card.Root>
        <Card.Header>
          <div class="flex items-start justify-between gap-4">
            <div class="flex items-start gap-3 flex-1 min-w-0">
              <div
                class="mt-0.5 flex h-9 w-9 shrink-0 items-center justify-center rounded-lg bg-muted"
              >
                <MonitorSmartphone class="h-4.5 w-4.5 text-muted-foreground" />
              </div>
              <div class="space-y-1 min-w-0">
                <Card.Title class="flex items-center gap-2 flex-wrap">
                  <span class="truncate">
                    {device.deviceDescription ?? "Unknown device"}
                  </span>
                  {#if device.isCurrent}
                    <Badge variant="secondary" class="shrink-0">
                      This device
                    </Badge>
                  {/if}
                </Card.Title>
                <Card.Description>
                  <span
                    class="flex flex-wrap gap-x-4 gap-y-1 text-xs text-muted-foreground"
                  >
                    {#if device.createdAt}
                      <span>Trusted {formatMediumDate(device.createdAt)}</span>
                    {/if}
                    {#if device.expiresAt}
                      <span class="flex items-center gap-1.5">
                        <Clock class="h-3 w-3" />
                        Expires {formatMediumDate(device.expiresAt)}
                      </span>
                    {/if}
                  </span>
                </Card.Description>
              </div>
            </div>
            <ConfirmDialog
              title="Remove trusted device"
              confirmLabel="Remove"
              onConfirm={() => handleRemove(device.id!)}
            >
              {#snippet trigger(props)}
                <Button
                  {...props}
                  type="button"
                  variant="outline-destructive"
                  size="sm"
                  class="shrink-0"
                  disabled={isRemoving === device.id}
                >
                  {#if isRemoving === device.id}
                    <LoaderCircle class="mr-1.5 h-3.5 w-3.5 animate-spin" />
                  {:else}
                    <Trash2 class="mr-1.5 h-3.5 w-3.5" />
                  {/if}
                  Remove
                </Button>
              {/snippet}
              {#snippet description()}
                The next sign-in on {device.deviceDescription ?? "this device"}
                will need an authenticator code. The device stays signed in.
              {/snippet}
            </ConfirmDialog>
          </div>
        </Card.Header>
      </Card.Root>
    {/each}
  {/if}
</div>
