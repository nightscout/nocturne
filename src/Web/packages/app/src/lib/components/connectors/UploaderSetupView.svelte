<script lang="ts">
  import { onDestroy, untrack } from "svelte";
  import type {
    UploaderApp,
    UploaderSetupResponse,
    DataSourceInfo,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { getUploaderName, getUploaderDescription } from "#lib/utils/uploader-labels.js";
  import { getActiveDataSources } from "#lib/api/generated/services.generated.remote.js";
  import { create as createGrant } from "#lib/api/generated/directGrants.generated.remote.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import AlertCircle from "@lucide/svelte/icons/circle-alert";
  import CheckCircle from "@lucide/svelte/icons/circle-check-big";
  import ChevronLeft from "@lucide/svelte/icons/chevron-left";
  import Check from "@lucide/svelte/icons/check";
  import Copy from "@lucide/svelte/icons/copy";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Clock from "@lucide/svelte/icons/clock";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import DeviceCodeApproval from "#lib/components/oauth/DeviceCodeApproval.svelte";
  import QRCode from "qrcode";

  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";

  import { describeSubmitError } from "#lib/forms/submit-error.js";
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


  let qrCodeDataUrl = $state<string | null>(null);
  let deviceApproved = $state(false);

  // ── API key generation (for apps without OAuth connectUrl) ────

  let apiToken = $state<string | null>(null);
  let apiTokenLoading = $state(false);
  let apiTokenError = $state<string | null>(null);
  const copy = createCopyFeedback();

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
      if (apiToken) startPolling();
    } catch (err) {
      apiTokenError = describeSubmitError(
        err,
        "Failed to generate API key. You can create one manually in Settings."
      );
    } finally {
      apiTokenLoading = false;
    }
  }

  // ── Connection polling ────────────────────────────────────────

  let pollInterval: ReturnType<typeof setInterval> | null = null;
  let destroyed = false;

  const dataSourcesQuery = getActiveDataSources();
  const dataSources = $derived<DataSourceInfo[]>(dataSourcesQuery.current ?? []);

  onDestroy(() => {
    destroyed = true;
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
      // Local rendering, not a request — there is no server reason to surface.
      qrCodeDataUrl = null;
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
    if (destroyed) return;
    stopPolling();
    pollInterval = setInterval(async () => {
      try {
        await dataSourcesQuery.refresh();

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

  $effect(() => {
    const response = setupResponse;
    untrack(() => {
      if (response?.connectUrl) {
        generateQrCode(response.connectUrl);
      } else if (response) {
        generateApiToken();
      }
    });
  });
</script>

<div class="space-y-4">
  <Button
    variant="ghost"
    size="sm"
    class="-ml-2"
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
      {#if !deviceApproved}
        <Card.Root>
          <Card.Header class="text-center pb-3">
            <Card.Title class="text-sm">Scan to connect</Card.Title>
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
      {/if}

      <Card.Root>
        {#if !deviceApproved}
          <Card.Header class="pb-3">
            <Card.Title class="text-sm">Enter authorization code</Card.Title>
            <Card.Description>
              After scanning, {app ? getUploaderName(app) : ''} will show an 8-character code. Enter it here to approve the connection.
            </Card.Description>
          </Card.Header>
        {/if}
        <Card.Content>
          <DeviceCodeApproval
            onApproved={() => {
              deviceApproved = true;
              startPolling();
            }}
          />
        </Card.Content>
      </Card.Root>

      {#if deviceApproved}
        {@const detected = isDetected(app?.id)}
        {@const ds = getDataSource(app?.id)}
        {#if detected && ds}
          <Card.Root variant="success">
            <Card.Content class="flex items-center gap-3 pt-6">
              <CheckCircle class="h-5 w-5 text-success" />
              <div>
                <p class="font-medium text-success">Receiving data</p>
                <p class="text-sm text-muted-foreground">
                  {app ? getUploaderName(app) : ''} is sending data. {ds.entriesLast24h ?? 0} entries in the last 24 hours.
                </p>
              </div>
            </Card.Content>
          </Card.Root>

          <div class="flex justify-end">
            <Button onclick={onConnected}>Continue to dashboard</Button>
          </div>
        {:else}
          <Card.Root>
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
      {/if}

    {:else}
      <!-- ── Manual setup (API key + Base URL) ── -->
      <Card.Root>
        <Card.Header class="pb-3">
          <Card.Title class="text-sm">Connection details</Card.Title>
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
                onclick={() => setupResponse?.baseUrl && copy.copy(setupResponse.baseUrl, 'url')}
              >
                {#if copy.isCopied('url')}
                  <Check class="h-4 w-4 text-success" />
                {:else}
                  <Copy class="h-4 w-4" />
                {/if}
              </Button>
            </div>
          </div>

          <!-- API Key -->
          <div class="space-y-1.5">
            <p class="text-xs font-medium text-muted-foreground">API key</p>
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
                  onclick={() => apiToken && copy.copy(apiToken, 'token')}
                >
                  {#if copy.isCopied('token')}
                    <Check class="h-4 w-4 text-success" />
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
        <Card.Root variant="success">
          <Card.Content class="flex items-center gap-3 pt-6">
            <CheckCircle class="h-5 w-5 text-success" />
            <div>
              <p class="font-medium text-success">Receiving data</p>
              <p class="text-sm text-muted-foreground">
                {app ? getUploaderName(app) : ''} is sending data. {ds.entriesLast24h ?? 0} entries in the last 24 hours.
              </p>
            </div>
          </Card.Content>
        </Card.Root>

        <div class="flex justify-end">
          <Button onclick={onConnected}>Continue to dashboard</Button>
        </div>
      {:else}
        <Card.Root>
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
    <Card.Root variant="destructive">
      <Card.Content class="flex items-center gap-3 pt-6">
        <AlertCircle class="h-5 w-5 text-destructive" />
        <div>
          <p class="font-medium">Failed to load setup instructions</p>
          <p class="text-sm text-muted-foreground">
            Could not load setup details for {app ? getUploaderName(app) : ''}. Try again.
          </p>
        </div>
      </Card.Content>
    </Card.Root>
  {/if}
</div>
