<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import DeviceAppClaimNotice from "#lib/components/auth/DeviceAppClaimNotice.svelte";
  import DeletionDisclosure from "#lib/components/auth/DeletionDisclosure.svelte";
  import DeviceScopeList from "#lib/components/oauth/DeviceScopeList.svelte";
  import CheckCircle from "@lucide/svelte/icons/circle-check-big";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Shield from "@lucide/svelte/icons/shield";
  import X from "@lucide/svelte/icons/x";
  import type { DeviceCodeInfo } from "#lib/api/generated/nocturne-api-client.js";
  import { getDeviceInfo } from "#routes/(authenticated)/oauth/oauth.remote.js";
  import { deviceApprove } from "#lib/api/generated/oAuths.generated.remote.js";
  import FormError from "#lib/forms/FormError.svelte";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { fresh } from "#lib/api/fresh-query.js";

  let { onApproved }: { onApproved?: () => void } = $props();

  type Stage = "entry" | "consent" | "approved" | "denied";

  let stage = $state<Stage>("entry");
  let codeInput = $state("");
  let lookupLoading = $state(false);
  let deciding = $state(false);
  let error = $state<string | null>(null);
  let device = $state<DeviceCodeInfo | null>(null);
  const claimedName = $derived(device ? (device.clientDisplayName ?? device.clientId ?? "") : "");
  const scopes = $derived((device?.scopes ?? []).filter(Boolean));

  async function lookup(e: SubmitEvent) {
    e.preventDefault();
    const code = codeInput.trim();
    if (!code) return;

    lookupLoading = true;
    error = null;
    try {
      const info = await fresh(getDeviceInfo({ userCode: code }));
      if (!info) {
        error = "Invalid or expired device code. Check the code and try again.";
        return;
      }
      device = info;
      stage = "consent";
    } catch (err) {
      error = describeSubmitError(
        err,
        "Invalid or expired device code. Check the code and try again."
      );
    } finally {
      lookupLoading = false;
    }
  }

  async function decide(approved: boolean) {
    if (!device) return;
    deciding = true;
    error = null;
    try {
      await deviceApprove({ user_code: device.userCode ?? codeInput.trim(), approved });
      stage = approved ? "approved" : "denied";
    } catch (err) {
      error = describeSubmitError(
        err,
        approved
          ? "Could not approve the device. The code possibly expired."
          : "Failed to deny the request."
      );
      return;
    } finally {
      deciding = false;
    }
    if (approved) onApproved?.();
  }
</script>

{#if stage === "approved"}
  <div
    role="status"
    aria-live="polite"
    aria-atomic="true"
    class="flex items-center gap-2 rounded bg-success/10 p-3 text-sm text-success"
  >
    <CheckCircle class="h-4 w-4 shrink-0" />
    <div>
      <p class="font-medium">Device authorized successfully.</p>
      <p>You can return to your device.</p>
    </div>
  </div>
{:else if stage === "denied"}
  <div
    role="status"
    aria-live="polite"
    aria-atomic="true"
    class="bg-muted flex items-center gap-2 rounded p-3 text-sm"
  >
    <X class="h-4 w-4" />
    Authorization denied. The device will not be granted access.
  </div>
{:else if stage === "consent" && device}
  <div class="space-y-4 rounded-md border p-3">
    <div class="flex items-center gap-2">
      <Shield class="h-4 w-4 text-primary" />
      <p class="text-sm font-medium">A device wants access</p>
    </div>

    <DeviceAppClaimNotice {claimedName} />

    <div>
      <p class="mb-2 text-sm font-medium">This application is requesting permission to:</p>
      <DeviceScopeList {scopes} />
    </div>

    <DeletionDisclosure access={device.deletionAccess} />

    <FormError issues={error} />

    <div class="flex gap-2">
      <Button
        variant="outline"
        size="sm"
        class="flex-1"
        disabled={deciding}
        onclick={() => decide(false)}
      >
        {#if deciding}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Deny
      </Button>
      <Button size="sm" class="flex-1" disabled={deciding} onclick={() => decide(true)}>
        {#if deciding}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Approve
      </Button>
    </div>
  </div>
{:else}
  <div class="space-y-3">
    <FormError issues={error} />

    <form onsubmit={lookup} class="flex items-center gap-2">
      <Input
        type="text"
        placeholder="XXXX-YYYY"
        maxlength={9}
        autocomplete="off"
        variant="code"
        bind:value={codeInput}
        disabled={lookupLoading}
      />
      <Button type="submit" size="sm" disabled={lookupLoading || !codeInput.trim()}>
        {#if lookupLoading}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {/if}
        Continue
      </Button>
    </form>
  </div>
{/if}
