<script lang="ts">
  import QRCode from "qrcode";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import QrCode from "@lucide/svelte/icons/qr-code";
  import { create as createGrant } from "#lib/api/generated/directGrants.generated.remote.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import { confirmIdentity } from "#lib/components/auth/confirm-identity.svelte.js";
  import { buildAapsClientSetupCode } from "#lib/utils/aaps-client-setup.js";

  interface Props {
    /** Origin URL of the Nocturne instance (trailing slash tolerated). */
    instanceUrl: string;
  }

  let { instanceUrl }: Props = $props();

  /**
   * AAPSClient sends each command to its master as a document in the v3 `settings` collection, which Nocturne
   * guards with the therapy scope. That scope also covers the profiles: no narrower one exists.
   */
  let allowCommands = $state(false);
  let qrDataUrl = $state<string | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);

  async function makeCode() {
    if (loading) return;
    loading = true;
    error = null;
    try {
      const result = await createGrant({
        label: "AAPSClient",
        scopes: allowCommands ? ["health.read", "therapy.readwrite"] : ["health.read"],
      });
      if (!result.token) throw new Error("No token in the response");
      // Error correction M, as AAPS draws its own code: the payload is long, and a denser code is harder to scan.
      qrDataUrl = await QRCode.toDataURL(buildAapsClientSetupCode(instanceUrl, result.token), {
        width: 240,
        margin: 2,
        errorCorrectionLevel: "M",
        color: { dark: "#000000", light: "#ffffff" },
      });
    } catch (err) {
      confirmIdentity.askAfterRefusal(err);
      error = describeSubmitError(
        err,
        "Could not make the QR code. Try again.",
      );
    } finally {
      loading = false;
    }
  }
</script>

<div class="space-y-4">
  <div>
    <p class="text-sm font-medium">Connect AAPSClient with a QR code</p>
    <p class="text-muted-foreground text-sm">
      For a follower phone with AAPSClient 4.0 or later. The phone gets its own API token and can
      read your data.
    </p>
  </div>

  {#if qrDataUrl}
    <div class="flex justify-center">
      <img
        src={qrDataUrl}
        alt="QR code to connect AAPSClient"
        width="240"
        height="240"
        class="rounded"
      />
    </div>
    <p class="text-muted-foreground text-center text-sm">
      In AAPSClient, open the setup wizard or Pair with master, then select Scan the QR code.
    </p>
    <p class="text-muted-foreground text-center text-sm">
      {#if allowCommands}
        This phone can read your data and send commands to your AAPS phone.
      {:else}
        This phone can only read your data.
      {/if}
    </p>
    <p class="text-muted-foreground text-center text-xs">
      Anyone who sees this code gets the same access. Show it only to the new phone. It cannot be
      shown again.
    </p>
  {:else}
    <div class="flex items-start justify-between gap-4">
      <div class="space-y-0.5 min-w-0">
        <Label for="aaps-client-commands">Let this phone send commands to your AAPS phone</Label>
        <p class="text-muted-foreground text-sm">
          After you pair it in AAPS, the phone can ask your AAPS phone for a bolus, a temporary
          target, or a profile switch. Your AAPS phone checks each command before it does it. The
          API token can then also change the treatment profiles in Nocturne. When this is off, the
          phone cannot pair and can only read your data.
        </p>
      </div>
      <Switch id="aaps-client-commands" bind:checked={allowCommands} disabled={loading} />
    </div>
    <Button variant="outline" size="sm" onclick={makeCode} disabled={loading}>
      {#if loading}
        <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
      {:else}
        <QrCode class="mr-1.5 h-4 w-4" />
      {/if}
      Make QR code
    </Button>
    {#if error}
      <p class="text-sm text-destructive">{error}</p>
    {/if}
  {/if}
</div>
