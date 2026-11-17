<script lang="ts">
  import { onMount } from "svelte";
  import { browser } from "$app/env";
  import QRCode from "qrcode";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import DeviceCodeApproval from "#lib/components/oauth/DeviceCodeApproval.svelte";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import { buildPreludeDeepLink, buildConnectPageUrl } from "#lib/utils/prelude-links.js";

  interface Props {
    /** Origin URL of the Nocturne instance (trailing slash tolerated). */
    instanceUrl: string;
  }

  let { instanceUrl }: Props = $props();

  let qrDataUrl = $state<string | null>(null);
  let isAndroid = $state(false);

  const connectPageUrl = $derived(buildConnectPageUrl(instanceUrl));
  const deepLink = $derived(buildPreludeDeepLink(instanceUrl));

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
</script>

<div class="space-y-4">
  <div>
    <p class="text-sm font-medium">Quick connect</p>
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
      In Prelude, select Settings → Account → Connect with QR and scan this code
    </p>
  {/if}

  <Separator />

  <div class="space-y-3">
    <div>
      <p class="text-sm font-medium">Enter authorization code</p>
      <p class="text-muted-foreground text-sm">
        Prelude displays a short code after it scans. Enter it here to approve.
      </p>
    </div>

    <DeviceCodeApproval />
  </div>
</div>
