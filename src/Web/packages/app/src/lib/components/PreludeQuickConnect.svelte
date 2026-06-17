<script lang="ts">
  import { onMount } from "svelte";
  import { browser } from "$app/environment";
  import QRCode from "qrcode";
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
  import { buildPreludeDeepLink, buildConnectPageUrl } from "$lib/utils/prelude-links";
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

  const connectPageUrl = $derived(buildConnectPageUrl(instanceUrl));
  const deepLink = $derived(buildPreludeDeepLink(instanceUrl));

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
      console.warn("[PreludeQuickConnect] QR code generation failed:", err);
    }
  });

  async function lookupDeviceCode() {
    const code = deviceCodeInput.trim();
    if (!code) return;

    deviceLookupLoading = true;
    deviceLookupError = null;

    try {
      const info = await getDeviceInfo({ userCode: code }).run();
      if (!info) {
        deviceLookupError = "Invalid or expired device code.";
        return;
      }
      deviceInfo = {
        userCode: info.userCode ?? code,
        clientId: info.clientId ?? "",
        displayName: info.clientDisplayName ?? null,
        isKnown: info.isKnownClient ?? false,
        scopes: (info.scopes ?? []).filter(Boolean),
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
      Scan the QR with Prelude, then approve the code it shows you here.
    </p>
  </div>

  {#if isAndroid}
    <Button href={deepLink} class="w-full">
      <Smartphone class="mr-2 h-4 w-4" />
      Open in Prelude
    </Button>
    {#if qrDataUrl}
      <details class="text-sm">
        <summary class="text-muted-foreground cursor-pointer">
          Or scan from another device
        </summary>
        <div class="flex justify-center pt-3">
          <img
            src={qrDataUrl}
            alt="QR code to connect Prelude"
            width="200"
            height="200"
            class="rounded"
          />
        </div>
      </details>
    {/if}
  {:else if qrDataUrl}
    <div class="flex justify-center">
      <img
        src={qrDataUrl}
        alt="QR code to connect Prelude"
        width="200"
        height="200"
        class="rounded"
      />
    </div>
    <p class="text-muted-foreground text-center text-sm">
      In Prelude, tap Settings → Account → Connect with QR and scan this code
    </p>
  {/if}

  <Separator />

  <!-- Device Authorization Code Section -->
  <div class="space-y-3">
    <div>
      <p class="text-sm font-medium">Enter Authorization Code</p>
      <p class="text-muted-foreground text-sm">
        Prelude displays a short code after it scans. Enter it here to approve.
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
        Device authorized successfully. Prelude is now connected.
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
            {#if deviceApproveLoading}
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
          placeholder="XXXX-YYYY"
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
</div>
