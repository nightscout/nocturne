<script lang="ts">
  import { enhance } from "$app/forms";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Shield,
    ShieldAlert,
    AlertTriangle,
    ExternalLink,
    Check,
    X,
    Smartphone,
  } from "lucide-svelte";

  const { data, form } = $props();

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

  const deviceInfo = $derived(
    form && "deviceInfo" in form ? form.deviceInfo : null
  );
  const approved = $derived(
    form && "action" in form && form.action === "approve" && "success" in form && form.success
  );
  const denied = $derived(
    form && "action" in form && form.action === "deny" && "denied" in form && form.denied
  );
  const formError = $derived(
    form && "error" in form ? (form.error as string) : null
  );

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
        {:else if deviceInfo.homepage}
          <div
            class="flex items-center justify-center gap-2 text-sm text-muted-foreground"
          >
            <a
              href={deviceInfo.homepage as string}
              target="_blank"
              rel="noopener noreferrer"
              class="inline-flex items-center gap-1 hover:text-foreground"
            >
              {deviceInfo.homepage}
              <ExternalLink class="h-3 w-3" />
            </a>
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

        {#if formError}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{formError}</p>
          </div>
        {/if}

        <form method="POST" use:enhance class="flex gap-3">
          <input type="hidden" name="user_code" value={deviceInfo.userCode} />

          <Button
            type="submit"
            formaction="?/deny"
            variant="outline"
            class="flex-1"
          >
            Deny
          </Button>
          <Button type="submit" formaction="?/approve" class="flex-1">
            Approve
          </Button>
        </form>
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
        {#if formError}
          <div
            class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{formError}</p>
          </div>
        {/if}

        <form method="POST" action="?/lookup" use:enhance class="space-y-4">
          <Input
            type="text"
            name="user_code"
            placeholder="XXXX-YYYY"
            maxlength={9}
            autocomplete="off"
            class="text-center text-lg tracking-widest uppercase"
            bind:value={codeInput}
          />
          <Button type="submit" class="w-full">
            Continue
          </Button>
        </form>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
