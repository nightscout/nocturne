<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Shield,
    ShieldAlert,
    AlertTriangle,
    Check,
    X,
    Smartphone,
    Loader2,
  } from "lucide-svelte";
  import {
    lookupDeviceForm,
    approveDeviceForm,
    denyDeviceForm,
  } from "../oauth.remote";

  const { data } = $props();

  /** Human-readable descriptions for each OAuth scope. */
  const scopeDescriptions: Record<string, string> = {
    "entries.read": "View your glucose readings",
    "entries.readwrite": "View and record glucose readings",
    "treatments.read": "View your treatments",
    "treatments.readwrite": "View and record treatments",
    "devicestatus.read": "View device status",
    "devicestatus.readwrite": "View and update device status",
    "profile.read": "View your profile settings",
    "profile.readwrite": "View and update profile settings",
    "notifications.read": "View notifications",
    "notifications.readwrite": "Manage notifications",
    "reports.read": "View reports and analytics",
    "identity.read": "View basic account info",
    "sharing.readwrite": "Manage sharing settings",
    "health.read": "View all health data (read-only)",
    "*": "Full access including delete",
  };

  let codeInput = $state(data.prefilledCode ?? "");

  // Get device info from lookup form result
  const deviceInfo = $derived(
    lookupDeviceForm.result && "deviceInfo" in lookupDeviceForm.result
      ? lookupDeviceForm.result.deviceInfo
      : null
  );

  // Check for approval/denial results
  const approved = $derived(
    approveDeviceForm.result &&
      "success" in approveDeviceForm.result &&
      approveDeviceForm.result.success
  );
  const denied = $derived(
    denyDeviceForm.result &&
      "denied" in denyDeviceForm.result &&
      denyDeviceForm.result.denied
  );

  // Collect all form-level issues
  const allIssues = $derived([
    ...(lookupDeviceForm.fields.allIssues() ?? []),
    ...(approveDeviceForm.fields.allIssues() ?? []),
    ...(denyDeviceForm.fields.allIssues() ?? []),
  ]);

  const scopes = $derived(
    deviceInfo ? (deviceInfo.scopes as string[]).filter(Boolean) : []
  );
  const hasFullAccess = $derived(scopes.includes("*"));
  const appName = $derived(
    deviceInfo
      ? (deviceInfo.displayName as string | null) ??
          (deviceInfo.clientId as string)
      : ""
  );
</script>

<svelte:head>
  <title>{deviceInfo ? `Authorize ${appName}` : "Link a Device"} - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center bg-background p-4">
  <Card.Root class="w-full max-w-md">
    {#if approved}
      <!-- State 3: Success -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30"
        >
          <Check class="h-6 w-6 text-green-600 dark:text-green-400" />
        </div>
        <Card.Title class="text-2xl font-bold">
          Device Authorized
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
          Authorization Denied
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
          Authorize Application
        </Card.Title>
        <Card.Description>
          <span class="font-semibold text-foreground">{appName}</span> wants to access
          your Nocturne data.
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        {#if !deviceInfo.isKnown}
          <div
            class="flex items-start gap-3 rounded-md border border-yellow-200 bg-yellow-50 p-3 dark:border-yellow-900/50 dark:bg-yellow-900/20"
          >
            <AlertTriangle
              class="mt-0.5 h-4 w-4 shrink-0 text-yellow-600 dark:text-yellow-400"
            />
            <p class="text-sm text-yellow-800 dark:text-yellow-200">
              This application is not in the Nocturne known app directory. Only
              approve if you trust this application.
            </p>
          </div>
        {/if}

        <Separator />

        <div>
          <p class="mb-3 text-sm font-medium text-foreground">
            This application is requesting permission to:
          </p>
          <ul class="space-y-2">
            {#each scopes as scope}
              <li class="flex items-start gap-3 text-sm">
                <Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                <span class="text-muted-foreground">
                  {scopeDescriptions[scope] ?? scope}
                </span>
              </li>
            {/each}
          </ul>
        </div>

        {#if !hasFullAccess}
          <div class="flex items-start gap-3 rounded-md bg-muted/50 p-3">
            <Shield class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
            <p class="text-sm text-muted-foreground">
              This app cannot delete your data.
            </p>
          </div>
        {:else}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">
              This app is requesting full access, including the ability to delete
              data.
            </p>
          </div>
        {/if}

        <Separator />

        {#each allIssues as issue}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{issue.message}</p>
          </div>
        {/each}

        <div class="flex gap-3">
          <form
            {...denyDeviceForm.enhance(async ({ submit }) => {
              await submit();
            })}
            class="flex-1"
          >
            <input type="hidden" name="user_code" value={deviceInfo.userCode} />
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
              await submit();
            })}
            class="flex-1"
          >
            <input type="hidden" name="user_code" value={deviceInfo.userCode} />
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
          Link a Device
        </Card.Title>
        <Card.Description>
          Enter the code shown on your device
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        {#each lookupDeviceForm.fields.allIssues() ?? [] as issue}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{issue.message}</p>
          </div>
        {/each}

        <form
          {...lookupDeviceForm.enhance(async ({ submit }) => {
            await submit();
          })}
          class="space-y-4"
        >
          <Input
            type="text"
            name="user_code"
            placeholder="XXXX-YYYY"
            maxlength={9}
            autocomplete="off"
            class="text-center text-lg tracking-widest uppercase"
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
