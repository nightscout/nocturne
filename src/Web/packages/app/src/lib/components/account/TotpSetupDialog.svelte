<script lang="ts">
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { AlertTriangle, Loader2 } from "lucide-svelte";
  import * as InputOTP from "$lib/components/ui/input-otp";
  import type { PinInput } from "bits-ui";

  interface Props {
    open?: boolean;
    qrDataUrl: string;
    secret: string;
    verifyCode?: string;
    label?: string;
    loading?: boolean;
    error?: string | null;
    onVerify: (code: string, label: string) => Promise<void>;
    onCancel: () => void;
  }

  let {
    open = $bindable(false),
    qrDataUrl,
    secret,
    verifyCode = $bindable(""),
    label = $bindable(""),
    loading = $bindable(false),
    error = $bindable<string | null>(null),
    onVerify,
    onCancel,
  }: Props = $props();

  async function handleVerify() {
    if (loading) return;
    if (verifyCode.length !== 6) return;
    await onVerify(verifyCode, label);
  }

  function handleCancel() {
    onCancel();
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>Set up authenticator app</Dialog.Title>
      <Dialog.Description>
        Scan the QR code with your authenticator app, then enter the 6-digit
        code to verify.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      {#if qrDataUrl}
        <div class="flex justify-center">
          <div class="rounded-md border bg-white p-2">
            <img src={qrDataUrl} alt="TOTP QR code" class="h-[200px] w-[200px]" />
          </div>
        </div>
      {/if}

      {#if secret}
        <div class="space-y-1">
          <p class="text-xs text-muted-foreground">
            Or enter this secret manually:
          </p>
          <p class="rounded-md border bg-muted/30 px-3 py-2 font-mono text-xs text-center select-all break-all">
            {secret}
          </p>
        </div>
      {/if}

      <div class="space-y-2">
        <Label for="totp-label">Label (optional)</Label>
        <Input
          id="totp-label"
          type="text"
          placeholder="e.g. Google Authenticator"
          bind:value={label}
        />
      </div>

      <div class="space-y-2">
        <Label>Verification code</Label>
        <div class="flex justify-center">
          <InputOTP.Root maxlength={6} bind:value={verifyCode} onComplete={handleVerify}>
            {#snippet children({ cells }: { cells: PinInput.CellProps["cell"][] })}
              <InputOTP.Group>
                {#each cells.slice(0, 3) as cell}
                  <InputOTP.Slot {cell} />
                {/each}
              </InputOTP.Group>
              <InputOTP.Separator />
              <InputOTP.Group>
                {#each cells.slice(3, 6) as cell}
                  <InputOTP.Slot {cell} />
                {/each}
              </InputOTP.Group>
            {/snippet}
          </InputOTP.Root>
        </div>
      </div>

      {#if error}
        <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
          <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
          <p class="text-sm text-destructive">{error}</p>
        </div>
      {/if}
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleCancel}>
        Cancel
      </Button>
      <Button
        disabled={loading || verifyCode.length !== 6}
        onclick={handleVerify}
      >
        {#if loading}
          <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
        {/if}
        Verify and save
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
