<script lang="ts">
  import { onDestroy } from "svelte";
  import type {
    UploaderApp,
    UploaderSetupResponse,
    DataSourceInfo,
  } from "$lib/api/generated/nocturne-api-client";
  import { getUploaderName, getUploaderDescription } from "$lib/utils/uploader-labels";
  import { getActiveDataSources } from "$api/generated/services.generated.remote";
  import { create as createGrant } from "$lib/api/generated/directGrants.generated.remote";
  import { getDeviceInfo } from "$routes/(authenticated)/oauth/oauth.remote";
  import { deviceApprove } from "$api/generated/oAuths.generated.remote";
  import { getOAuthScopeDescription } from "$lib/constants/oauth-scopes";
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    AlertCircle,
    CheckCircle,
    ChevronLeft,
    Check,
    Copy,
    Loader2,
    Clock,
    AlertTriangle,
    X,
    Shield,
    ShieldAlert,
  } from "lucide-svelte";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import QRCode from "qrcode";

  interface Props {
    app: UploaderApp | null;
    setupResponse: UploaderSetupResponse | null;
    onBack: () => void;
    onConnected: () => void;
  }

  const {
    app = null,
    setupResponse = null,
    onBack,
    onConnected,
  }: Props = $props();

  // ── Device authorization (OAuth) state ────────────────────────

  let qrCodeDataUrl = $state<string | null>(null);
  let deviceCodeInput = $state("");
  let deviceLookupLoading = $state(false);
  let deviceLookupError = $state<string | null>(null);
  let deviceInfo = $state<{
    userCode: string;
    clientId: string;
    displayName: string | null;
    isKnown: boolean;
    scopes: string[];
  } | null>(null);
  let deviceApproveLoading = $state(false);
  let deviceApproved = $state(false);
  let deviceDenied = $state(false);

  // ── API key generation (for apps without OAuth connectUrl) ────

  let apiToken = $state<string | null>(null);
  let apiTokenLoading = $state(false);
  let apiTokenError = $state<string | null>(null);
  let copiedField = $state<string | null>(null);

  async function generateApiToken() {
    if (!app || apiToken || apiTokenLoading) return;
    apiTokenLoading = true;
    apiTokenError = null;
    try {
      const result = await createGrant({
        label: getUploaderName(app),
        scopes: ["health.readwrite"],
      });
      apiToken = result.token ?? null;
    } catch {
      apiTokenError = "Failed to generate API key. You can create one manually in Settings.";
    } finally {
      apiTokenLoading = false;
    }
  }

  async function copyToClipboard(text: string, field: string) {
    await navigator.clipboard.writeText(text);
    copiedField = field;
    setTimeout(() => {
      copiedField = null;
    }, 2000);
  }

  // ── Connection polling ────────────────────────────────────────

  let pollInterval = $state<ReturnType<typeof setInterval> | null>(null);
  let dataSources = $state<DataSourceInfo[]>([]);

  onDestroy(() => {
    stopPolling();
  });

  async function generateQrCode(url: string) {
    try {
      qrCodeDataUrl = await QRCode.toDataURL(url, {
        width: 200,
        margin: 2,
        color: { dark: "#000000", light: "#ffffff" },
      });
    } catch {
      qrCodeDataUrl = null;
    }
  }

  async function lookupDeviceCode() {
    const code = deviceCodeInput.trim();
    if (!code) return;

    deviceLookupLoading = true;
    deviceLookupError = null;

    try {
      const info = await getDeviceInfo({ userCode: code });
      if (!info) {
        deviceLookupError = "Invalid or expired device code.";
        return;
      }
      deviceInfo = {
        userCode: info.userCode ?? code,
        clientId: info.clientId ?? "",
        displayName: info.clientDisplayName ?? null,
        isKnown: info.isKnownClient ?? false,
        scopes: (info.scopes ?? []).filter(Boolean) as string[],
      };
    } catch {
      deviceLookupError = "Invalid or expired device code. Please check and try again.";
    } finally {
      deviceLookupLoading = false;
    }
  }

  async function handleApproveDevice() {
    if (!deviceInfo) return;
    deviceApproveLoading = true;
    try {
      await deviceApprove({ user_code: deviceInfo.userCode, approved: true });
      deviceApproved = true;
      startPolling();
    } catch {
      deviceLookupError = "Failed to approve. The code may have expired.";
    } finally {
      deviceApproveLoading = false;
    }
  }

  async function handleDenyDevice() {
    if (!deviceInfo) return;
    deviceApproveLoading = true;
    try {
      await deviceApprove({ user_code: deviceInfo.userCode, approved: false });
      deviceDenied = true;
    } catch {
      deviceLookupError = "Failed to deny the request.";
    } finally {
      deviceApproveLoading = false;
    }
  }

  function isDetected(appId: string | undefined): boolean {
    if (!appId) return false;
    return dataSources.some(
      (ds) => ds.sourceType?.toLowerCase() === appId.toLowerCase(),
    );
  }

  function getDataSource(appId: string | undefined): DataSourceInfo | undefined {
    if (!appId) return undefined;
    return dataSources.find(
      (ds) => ds.sourceType?.toLowerCase() === appId.toLowerCase(),
    );
  }

  function startPolling() {
    stopPolling();
    pollInterval = setInterval(async () => {
      try {
        const sources = await getActiveDataSources();
        dataSources = sources ?? [];

        // Stop polling if we detect data from the selected app
        if (app && isDetected(app.id)) {
          stopPolling();
        }
      } catch {
        // Silently continue polling
      }
    }, 15_000);
  }

  function stopPolling() {
    if (pollInterval) {
      clearInterval(pollInterval);
      pollInterval = null;
    }
  }

  // Initialize QR code or generate API key when setupResponse arrives
  $effect(() => {
    if (setupResponse?.connectUrl) {
      generateQrCode(setupResponse.connectUrl);
    } else if (setupResponse && !setupResponse.connectUrl) {
      generateApiToken();
    }
  });

  // Start polling for data once the API key has been generated
  $effect(() => {
    if (apiToken) {
      startPolling();
    }
  });
</script>

<div class="space-y-4">
  <Button
    variant="ghost"
    size="sm"
    class="gap-1 -ml-2"
    onclick={onBack}
  >
    <ChevronLeft class="h-4 w-4" />
    Back to data sources
  </Button>

  {#if setupResponse}
    <div>
      <h2 class="text-lg font-semibold">{app ? getUploaderName(app) : ''}</h2>
      {#if app && getUploaderDescription(app)}
        <p class="text-sm text-muted-foreground">
          {getUploaderDescription(app)}
        </p>
      {/if}
    </div>

    {#if setupResponse.connectUrl}
      <!-- ── OAuth Device Flow (QR code + inline authorization) ── -->
      {#if deviceApproved}
        <!-- Approved — waiting for data -->
        <Card.Root class="border-green-500/30">
          <Card.Content class="space-y-2 pt-6">
            <div class="flex items-center gap-3">
              <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30">
                <Check class="h-5 w-5 text-green-600 dark:text-green-400" />
              </div>
              <div>
                <p class="font-medium text-green-600">Device Authorized</p>
                <p class="text-sm text-muted-foreground">
                  {app ? getUploaderName(app) : ''} is now connected. You can return to your device.
                </p>
              </div>
            </div>
          </Card.Content>
        </Card.Root>

        <!-- Connection status (polling for data) -->
        {@const detected = isDetected(app?.id)}
        {@const ds = getDataSource(app?.id)}
        {#if detected && ds}
          <Card.Root class="border-green-500/30">
            <Card.Content class="flex items-center gap-3 pt-6">
              <CheckCircle class="h-5 w-5 text-green-500" />
              <div>
                <p class="font-medium text-green-600">Receiving Data</p>
                <p class="text-sm text-muted-foreground">
                  {app ? getUploaderName(app) : ''} is sending data. {ds.entriesLast24h ?? 0} entries in the last 24 hours.
                </p>
              </div>
            </Card.Content>
          </Card.Root>

          <div class="flex justify-end">
            <Button onclick={onConnected}>Continue to Dashboard</Button>
          </div>
        {:else}
          <Card.Root class="border-muted">
            <Card.Content class="flex items-center gap-3 pt-6">
              <Clock class="h-5 w-5 text-muted-foreground" />
              <div>
                <p class="font-medium">Waiting for data</p>
                <p class="text-sm text-muted-foreground">
                  It can take a few minutes for the first glucose reading to arrive. This page will update automatically.
                </p>
              </div>
            </Card.Content>
          </Card.Root>
        {/if}

      {:else if deviceDenied}
        <!-- Denied -->
        <Card.Root>
          <Card.Content class="flex items-center gap-3 pt-6">
            <div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-muted">
              <X class="h-5 w-5 text-muted-foreground" />
            </div>
            <div>
              <p class="font-medium">Authorization Denied</p>
              <p class="text-sm text-muted-foreground">The device will not be granted access.</p>
            </div>
          </Card.Content>
        </Card.Root>

      {:else if deviceInfo}
        <!-- Step 2: Approve / deny the device -->
        <Card.Root>
          <Card.Header class="space-y-1 text-center">
            <div class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
              <Shield class="h-6 w-6 text-primary" />
            </div>
            <Card.Title class="text-lg">Authorize Application</Card.Title>
            <Card.Description>
              <span class="font-semibold text-foreground">{deviceInfo.displayName ?? deviceInfo.clientId}</span>
              wants to access your Nocturne data.
            </Card.Description>
          </Card.Header>
          <Card.Content class="space-y-4">
            {#if !deviceInfo.isKnown}
              <div class="flex items-start gap-3 rounded-md border border-yellow-200 bg-yellow-50 p-3 dark:border-yellow-900/50 dark:bg-yellow-900/20">
                <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-yellow-600 dark:text-yellow-400" />
                <p class="text-sm text-yellow-800 dark:text-yellow-200">
                  This application is not in the Nocturne known app directory. Only approve if you trust it.
                </p>
              </div>
            {/if}

            <Separator />

            <div>
              <p class="mb-3 text-sm font-medium">This application is requesting permission to:</p>
              <ul class="space-y-2">
                {#each deviceInfo.scopes as scope}
                  <li class="flex items-start gap-3 text-sm">
                    <Check class="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                    <span class="text-muted-foreground">{getOAuthScopeDescription(scope)}</span>
                  </li>
                {/each}
              </ul>
            </div>

            {#if deviceInfo.scopes.includes("*")}
              <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
                <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                <p class="text-sm text-destructive">This app is requesting full access, including the ability to delete data.</p>
              </div>
            {:else}
              <div class="flex items-start gap-3 rounded-md bg-muted/50 p-3">
                <Shield class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                <p class="text-sm text-muted-foreground">This app cannot delete your data.</p>
              </div>
            {/if}

            {#if deviceLookupError}
              <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
                <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                <p class="text-sm text-destructive">{deviceLookupError}</p>
              </div>
            {/if}

            <Separator />

            <div class="flex gap-3">
              <Button
                variant="outline"
                class="flex-1"
                disabled={deviceApproveLoading}
                onclick={handleDenyDevice}
              >
                {#if deviceApproveLoading}
                  <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                {/if}
                Deny
              </Button>
              <Button
                class="flex-1"
                disabled={deviceApproveLoading}
                onclick={handleApproveDevice}
              >
                {#if deviceApproveLoading}
                  <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                {/if}
                Approve
              </Button>
            </div>
          </Card.Content>
        </Card.Root>

      {:else}
        <!-- Step 1: QR code + code entry -->
        <Card.Root>
          <Card.Header class="text-center pb-3">
            <Card.Title class="text-sm">Scan to Connect</Card.Title>
            <Card.Description>
              Scan this QR code with your phone's camera to open {app ? getUploaderName(app) : ''} and start the connection.
            </Card.Description>
          </Card.Header>
          <Card.Content class="flex flex-col items-center gap-4">
            {#if qrCodeDataUrl}
              <div class="rounded-lg border bg-white p-2">
                <img src={qrCodeDataUrl} alt="QR code to connect {app ? getUploaderName(app) : ''}" class="h-48 w-48" />
              </div>
            {:else}
              <div class="flex h-48 w-48 items-center justify-center rounded-lg border bg-muted">
                <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
              </div>
            {/if}
          </Card.Content>
        </Card.Root>

        <Card.Root>
          <Card.Header class="pb-3">
            <Card.Title class="text-sm">Enter Authorization Code</Card.Title>
            <Card.Description>
              After scanning, {app ? getUploaderName(app) : ''} will show an 8-character code. Enter it here to approve the connection.
            </Card.Description>
          </Card.Header>
          <Card.Content class="space-y-4">
            {#if deviceLookupError}
              <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
                <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                <p class="text-sm text-destructive">{deviceLookupError}</p>
              </div>
            {/if}

            <form
              class="flex gap-2"
              onsubmit={(e) => {
                e.preventDefault();
                lookupDeviceCode();
              }}
            >
              <Input
                type="text"
                placeholder="XXXX-YYYY"
                maxlength={9}
                autocomplete="off"
                class="text-center text-lg tracking-widest uppercase"
                bind:value={deviceCodeInput}
                disabled={deviceLookupLoading}
              />
              <Button type="submit" disabled={deviceLookupLoading || !deviceCodeInput.trim()}>
                {#if deviceLookupLoading}
                  <Loader2 class="h-4 w-4 animate-spin" />
                {:else}
                  Continue
                {/if}
              </Button>
            </form>
          </Card.Content>
        </Card.Root>
      {/if}

    {:else}
      <!-- ── Manual setup (API key + Base URL) ── -->
      <Card.Root>
        <Card.Header class="pb-3">
          <Card.Title class="text-sm">Connection Details</Card.Title>
          <Card.Description>
            Enter these values in {app ? getUploaderName(app) : 'your app'}'s Nightscout settings.
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-4">
          <!-- Base URL -->
          <div class="space-y-1.5">
            <p class="text-xs font-medium text-muted-foreground">Nocturne URL</p>
            <div class="flex gap-2">
              <code class="flex-1 rounded-md bg-muted px-3 py-2 text-sm font-mono break-all">
                {setupResponse.baseUrl ?? ''}
              </code>
              <Button
                variant="outline"
                size="icon"
                onclick={() => setupResponse?.baseUrl && copyToClipboard(setupResponse.baseUrl, 'url')}
              >
                {#if copiedField === 'url'}
                  <Check class="h-4 w-4 text-green-500" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          <!-- API Key -->
          <div class="space-y-1.5">
            <p class="text-xs font-medium text-muted-foreground">API Key</p>
            {#if apiTokenLoading}
              <div class="flex items-center gap-2 rounded-md bg-muted px-3 py-2 text-sm text-muted-foreground">
                <Loader2 class="h-4 w-4 animate-spin" />
                Generating API key...
              </div>
            {:else if apiToken}
              <div class="flex gap-2">
                <code class="flex-1 rounded-md bg-muted px-3 py-2 text-sm font-mono break-all">
                  {apiToken}
                </code>
                <Button
                  variant="outline"
                  size="icon"
                  onclick={() => apiToken && copyToClipboard(apiToken, 'token')}
                >
                  {#if copiedField === 'token'}
                    <Check class="h-4 w-4 text-green-500" />
                  {:else}
                    <Copy class="h-4 w-4" />
                  {/if}
                </Button>
              </div>
              <p class="text-xs text-muted-foreground">
                Copy this now. It cannot be shown again.
              </p>
            {:else if apiTokenError}
              <div class="flex items-start gap-2 rounded-md border border-destructive/20 bg-destructive/5 p-3">
                <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
                <p class="text-sm text-destructive">{apiTokenError}</p>
              </div>
            {/if}
          </div>
        </Card.Content>
      </Card.Root>

      <!-- Polling for connection -->
      {@const detected = isDetected(app?.id)}
      {@const ds = getDataSource(app?.id)}
      {#if detected && ds}
        <Card.Root class="border-green-500/30">
          <Card.Content class="flex items-center gap-3 pt-6">
            <CheckCircle class="h-5 w-5 text-green-500" />
            <div>
              <p class="font-medium text-green-600">Receiving Data</p>
              <p class="text-sm text-muted-foreground">
                {app ? getUploaderName(app) : ''} is sending data. {ds.entriesLast24h ?? 0} entries in the last 24 hours.
              </p>
            </div>
          </Card.Content>
        </Card.Root>

        <div class="flex justify-end">
          <Button onclick={onConnected}>Continue to Dashboard</Button>
        </div>
      {:else}
        <Card.Root class="border-muted">
          <Card.Content class="flex items-center gap-3 pt-6">
            <Clock class="h-5 w-5 text-muted-foreground" />
            <div>
              <p class="font-medium">Waiting for data</p>
              <p class="text-sm text-muted-foreground">
                Configure {app ? getUploaderName(app) : 'your app'} with the details above, then this page will update when data arrives.
              </p>
            </div>
          </Card.Content>
        </Card.Root>
      {/if}
    {/if}
  {:else}
    <Card.Root class="border-destructive">
      <Card.Content class="flex items-center gap-3 pt-6">
        <AlertCircle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load setup instructions</p>
          <p class="text-sm text-muted-foreground">
            Could not load setup details for {app ? getUploaderName(app) : ''}. Please try again.
          </p>
        </div>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
