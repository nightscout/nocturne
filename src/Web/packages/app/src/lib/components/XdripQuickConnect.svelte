<script lang="ts">
  import { onMount, onDestroy } from "svelte";
  import { browser } from "$app/environment";
  import QRCode from "qrcode";
  import { getActiveDataSources } from "$api/generated/services.generated.remote";
  import type { DataSourceInfo } from "$lib/api/generated/nocturne-api-client";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Separator } from "$lib/components/ui/separator";
  import {
    Smartphone,
    CheckCircle,
    Loader2,
    Check,
    Shield,
    AlertTriangle,
    X,
  } from "lucide-svelte";
  import { buildXdripDeepLink, buildConnectPageUrl } from "$lib/utils/xdrip-links";
  import { getDeviceInfo } from "$routes/(authenticated)/oauth/oauth.remote";
  import { deviceApprove } from "$api/generated/oAuths.generated.remote";
  import { getOAuthScopeDescription } from "$lib/constants/oauth-scopes";

  interface Props {
    /** Origin URL of the Nocturne instance (trailing slash tolerated). */
    instanceUrl: string;
  }

  let { instanceUrl }: Props = $props();

  let qrDataUrl = $state<string | null>(null);
  let isAndroid = $state(false);

  // ── Connection polling state ───────────────────────────────────
  type ConnectionState = "waiting" | "connected" | "timeout";
  let connectionState = $state<ConnectionState>("waiting");
  let pollInterval: ReturnType<typeof setInterval> | null = null;
  let pollStartTime = 0;
  const POLL_INTERVAL_MS = 10_000;
  const POLL_TIMEOUT_MS = 60_000;

  const connectPageUrl = $derived(buildConnectPageUrl(instanceUrl));
  const deepLink = $derived(buildXdripDeepLink(instanceUrl));

  // ── Device authorization state ─────────────────────────────────
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

  const deviceAppName = $derived(
    deviceInfo ? (deviceInfo.displayName ?? deviceInfo.clientId) : "",
  );

  onMount(async () => {
    if (browser) {
      isAndroid = /android/i.test(navigator.userAgent);
    }
    try {
      qrDataUrl = await QRCode.toDataURL(connectPageUrl, {
        width: 200,
        margin: 2,
        color: { dark: "#000000", light: "#ffffff" },
      });
    } catch (err) {
      console.warn("[XdripQuickConnect] QR code generation failed:", err);
    }
    startPolling();
  });

  onDestroy(() => {
    stopPolling();
  });

  function isXdripDetected(sources: DataSourceInfo[]): boolean {
    return sources.some((ds) => ds.sourceType?.toLowerCase() === "xdrip");
  }

  async function checkStatus() {
    try {
      const sources = (await getActiveDataSources()) ?? [];
      if (isXdripDetected(sources)) {
        connectionState = "connected";
        stopPolling();
        return;
      }
      if (Date.now() - pollStartTime > POLL_TIMEOUT_MS) {
        connectionState = "timeout";
        stopPolling();
      }
    } catch (err) {
      console.warn("[XdripQuickConnect] Data source poll failed:", err);
    }
  }

  function startPolling() {
    stopPolling();
    connectionState = "waiting";
    pollStartTime = Date.now();
    checkStatus();
    pollInterval = setInterval(checkStatus, POLL_INTERVAL_MS);
  }

  function stopPolling() {
    if (pollInterval !== null) {
      clearInterval(pollInterval);
      pollInterval = null;
    }
  }

  function retryCheck() {
    startPolling();
  }

  // ── Device authorization handlers ─────────────────────────────

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

  function handleDeviceCodeSubmit(e: SubmitEvent) {
    e.preventDefault();
    lookupDeviceCode();
  }
</script>

<div class="space-y-4">
  <div>
    <p class="text-sm font-medium">Quick Connect</p>
    <p class="text-muted-foreground text-sm">
      Automatically configure xDrip+ with your Nocturne instance.
    </p>
  </div>

  {#if isAndroid}
    <Button href={deepLink} class="w-full">
      <Smartphone class="mr-2 h-4 w-4" />
      Open in xDrip+
    </Button>
    {#if qrDataUrl}
      <details class="text-sm">
        <summary class="text-muted-foreground cursor-pointer">
          Or scan from another device
        </summary>
        <div class="flex justify-center pt-3">
          <img
            src={qrDataUrl}
            alt="QR code to connect xDrip+"
            width="200"
            height="200"
            class="rounded"
          />
        </div>
      </details>
    {/if}
  {:else}
    {#if qrDataUrl}
      <div class="flex justify-center">
        <img
          src={qrDataUrl}
          alt="QR code to connect xDrip+"
          width="200"
          height="200"
          class="rounded"
        />
      </div>
      <p class="text-muted-foreground text-center text-sm">
        Scan this QR code with your phone's camera
      </p>
    {/if}
    <details class="text-sm">
      <summary class="text-muted-foreground cursor-pointer">
        Or open this link on your phone
      </summary>
      <code class="bg-muted mt-2 block rounded px-3 py-2 text-xs break-all">
        {connectPageUrl}
      </code>
    </details>
  {/if}

  <Separator />

  <!-- Device Authorization Code Section -->
  <div class="space-y-3">
    <div>
      <p class="text-sm font-medium">Enter Authorization Code</p>
      <p class="text-muted-foreground text-sm">
        If xDrip+ shows an authorization code, enter it here.
      </p>
    </div>

    {#if deviceApproved}
      <div
        role="status"
        aria-live="polite"
        aria-atomic="true"
        class="flex items-center gap-2 rounded bg-green-50 p-3 text-sm text-green-900 dark:bg-green-950 dark:text-green-100"
      >
        <CheckCircle class="h-4 w-4" />
        Device authorized successfully. Waiting for data...
      </div>
    {:else if deviceDenied}
      <div
        role="status"
        aria-live="polite"
        aria-atomic="true"
        class="bg-muted flex items-center gap-2 rounded p-3 text-sm"
      >
        <X class="h-4 w-4" />
        Authorization denied. The device will not be granted access.
      </div>
    {:else if deviceInfo}
      <!-- Consent / Approval -->
      <div class="space-y-3 rounded-md border p-3">
        <div class="flex items-center gap-2">
          <Shield class="h-4 w-4 text-primary" />
          <p class="text-sm font-medium">
            <span class="text-foreground font-semibold">{deviceAppName}</span> wants access
          </p>
        </div>

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

        <div>
          <p class="text-muted-foreground mb-2 text-xs font-medium">Requested permissions:</p>
          <ul class="space-y-1">
            {#each deviceInfo.scopes as scope (scope)}
              <li class="flex items-start gap-2 text-sm">
                <Check class="mt-0.5 h-3 w-3 shrink-0 text-primary" />
                <span class="text-muted-foreground">
                  {getOAuthScopeDescription(scope)}
                </span>
              </li>
            {/each}
          </ul>
        </div>

        {#if deviceLookupError}
          <div
            class="flex items-start gap-2 rounded-md border border-destructive/20 bg-destructive/5 p-2"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
            <p class="text-sm text-destructive">{deviceLookupError}</p>
          </div>
        {/if}

        <div class="flex gap-2">
          <Button
            variant="outline"
            size="sm"
            class="flex-1"
            disabled={deviceApproveLoading}
            onclick={handleDenyDevice}
          >
            {#if deviceApproveLoading && deviceDenied !== true}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            {/if}
            Deny
          </Button>
          <Button
            size="sm"
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
      </div>
    {:else}
      <!-- Code Entry -->
      {#if deviceLookupError}
        <div
          class="flex items-start gap-2 rounded-md border border-destructive/20 bg-destructive/5 p-2"
        >
          <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
          <p class="text-sm text-destructive">{deviceLookupError}</p>
        </div>
      {/if}

      <form onsubmit={handleDeviceCodeSubmit} class="flex items-center gap-2">
        <Input
          type="text"
          placeholder="XXXX-YY"
          maxlength={9}
          autocomplete="off"
          class="text-center uppercase tracking-widest"
          bind:value={deviceCodeInput}
          disabled={deviceLookupLoading}
        />
        <Button
          type="submit"
          size="sm"
          disabled={deviceLookupLoading || !deviceCodeInput.trim()}
        >
          {#if deviceLookupLoading}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
          {/if}
          Continue
        </Button>
      </form>
    {/if}
  </div>

  <Separator />

  <!-- Connection Status -->
  {#if connectionState === "waiting"}
    <div
      role="status"
      aria-live="polite"
      aria-atomic="true"
      class="bg-muted text-muted-foreground flex items-center gap-2 rounded p-3 text-sm"
    >
      <Loader2 class="h-4 w-4 animate-spin" />
      Waiting for data from xDrip+...
    </div>
  {:else if connectionState === "connected"}
    <div
      role="status"
      aria-live="polite"
      aria-atomic="true"
      class="flex items-center gap-2 rounded bg-green-50 p-3 text-sm text-green-900 dark:bg-green-950 dark:text-green-100"
    >
      <CheckCircle class="h-4 w-4" />
      Connected! Data is flowing from xDrip+.
    </div>
  {:else if connectionState === "timeout"}
    <div
      role="status"
      aria-live="polite"
      aria-atomic="true"
      class="bg-muted space-y-2 rounded p-3 text-sm"
    >
      <p>No data from xDrip+ yet. This is normal if xDrip+ hasn't had a new reading.</p>
      <Button variant="outline" size="sm" onclick={retryCheck}>
        Check now
      </Button>
    </div>
  {/if}
</div>
