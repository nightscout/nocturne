<script lang="ts">
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import Shield from "@lucide/svelte/icons/shield";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import Check from "@lucide/svelte/icons/check";
  import X from "@lucide/svelte/icons/x";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import {
    lookupDeviceForm,
    approveDeviceForm,
    denyDeviceForm,
  } from "../oauth.remote";
  import { FormError, useSubmission } from "#lib/forms/index.js";
  import DeviceAppClaimNotice from "#lib/components/auth/DeviceAppClaimNotice.svelte";
  import DeviceScopeList from "#lib/components/oauth/DeviceScopeList.svelte";
  import DeletionDisclosure from "#lib/components/auth/DeletionDisclosure.svelte";

  // Auth guard: redirect to login if not authenticated
  $effect(() => {
    if (!page.data.isAuthenticated) {
      const returnUrl = encodeURIComponent(page.url.pathname + page.url.search);
      goto(resolve(`auth/login?returnUrl=${returnUrl}`), { replace: true });
    }
  });

  // Read the prefilled code from URL instead of server data
  const prefilledCode = $derived(page.url.searchParams.get("user_code") ?? "");

  let codeInput = $state("");

  $effect(() => {
    if (prefilledCode) {
      codeInput = prefilledCode;
    }
  });

  // Get device info from lookup form result
  const deviceInfo = $derived(
    lookupDeviceForm.result && "deviceInfo" in lookupDeviceForm.result
      ? lookupDeviceForm.result.deviceInfo
      : null
  );

  // Check for approval/denial results
  const approved = $derived(approveDeviceForm.result && "success" in approveDeviceForm.result && approveDeviceForm.result.success);

  const denied = $derived(denyDeviceForm.result && "denied" in denyDeviceForm.result && denyDeviceForm.result.denied);

  const submission = useSubmission();

  // Collect all form-level issues
  const allIssues = $derived([
    ...lookupDeviceForm.fields.allIssues() ?? [],
    ...approveDeviceForm.fields.allIssues() ?? [],
    ...denyDeviceForm.fields.allIssues() ?? []
  ]);

  const scopes = $derived(
    deviceInfo ? (deviceInfo.scopes ?? []).filter(Boolean) : []
  );
  const appName = $derived(
    deviceInfo
      ? deviceInfo.clientDisplayName ?? deviceInfo.clientId ?? ""
      : ""
  );
</script>

<svelte:head>
  <title>{deviceInfo ? "Authorize a device" : "Link a device"} - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  <Card.Root class="w-full max-w-md">
    {#if approved}
      <!-- State 3: Success -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-success/10"
        >
          <Check class="h-6 w-6 text-success" />
        </div>
        <Card.Title class="text-2xl font-bold">
          Device authorized
        </Card.Title>
        <Card.Description>
          You can close this window and return to your device.
        </Card.Description>
      </Card.Header>
    {:else if denied}
      <!-- State 4: Denied -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-muted"
        >
          <X class="h-6 w-6 text-muted-foreground" />
        </div>
        <Card.Title class="text-2xl font-bold">
          Authorization denied
        </Card.Title>
        <Card.Description>
          The device will not be granted access.
        </Card.Description>
      </Card.Header>
    {:else if deviceInfo}
      <!-- State 2: Consent / Approval -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Shield class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">
          Authorize application
        </Card.Title>
        <Card.Description>
          A device wants to access your Nocturne data.
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        <DeviceAppClaimNotice claimedName={appName} />

        <Separator />

        <div>
          <p class="mb-3 text-sm font-medium text-foreground">
            This application is requesting permission to:
          </p>
          <DeviceScopeList {scopes} />
        </div>

        <DeletionDisclosure access={deviceInfo.deletionAccess} />

        <Separator />

        {#each allIssues as issue, i (i)}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{issue.message}</p>
          </div>
        {/each}

        <FormError issues={submission.error} />

        <div class="flex gap-3">
          <form
            {...denyDeviceForm.enhance(async ({ submit }) => {
              await submission.run(submit);
            })}
            class="flex-1"
          >
            <input {...denyDeviceForm.fields.user_code.as("hidden", deviceInfo.userCode ?? "")} />
            <Button
              type="submit"
              variant="outline"
              class="w-full"
              disabled={!!denyDeviceForm.pending}
            >
              {#if denyDeviceForm.pending}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Deny
            </Button>
          </form>
          <form
            {...approveDeviceForm.enhance(async ({ submit }) => {
              await submission.run(submit);
            })}
            class="flex-1"
          >
            <input {...approveDeviceForm.fields.user_code.as("hidden", deviceInfo.userCode ?? "")} />
            <Button
              type="submit"
              class="w-full"
              disabled={!!approveDeviceForm.pending}
            >
              {#if approveDeviceForm.pending}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              {/if}
              Approve
            </Button>
          </form>
        </div>
      </Card.Content>
    {:else}
      <!-- State 1: Code Entry -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Smartphone class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">
          Link a device
        </Card.Title>
        <Card.Description>
          Enter the code shown on your device
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        <FormError issues={submission.error} />
        {#each lookupDeviceForm.fields.allIssues() ?? [] as issue, i (i)}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{issue.message}</p>
          </div>
        {/each}

        <form
          {...lookupDeviceForm.enhance(async ({ submit }) => {
            await submission.run(submit);
          })}
          class="space-y-4"
        >
          <Input
            type="text"
            name={lookupDeviceForm.fields.user_code.as("text").name}
            placeholder="XXXX-YYYY"
            maxlength={9}
            autocomplete="off"
            variant="code"
            bind:value={codeInput}
            disabled={!!lookupDeviceForm.pending}
          />
          <Button
            type="submit"
            class="w-full"
            disabled={!!lookupDeviceForm.pending}
          >
            {#if lookupDeviceForm.pending}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Continue
          </Button>
        </form>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
