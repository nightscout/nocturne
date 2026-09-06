<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Checkbox } from "$lib/components/ui/checkbox";
  import { Check, Copy, Download, ShieldCheck, TriangleAlert } from "lucide-svelte";
  import { copyToClipboard } from "$lib/utils";

  interface Props {
    codes: string[];
    onContinue: () => void;
    continueLabel?: string;
  }

  let { codes, onContinue, continueLabel = "Continue" }: Props = $props();

  let copyFailed = $state(false);
  let copied = $state(false);
  let downloaded = $state(false);
  let confirmedWrittenDown = $state(false);

  /**
   * Losing these codes can lock someone out of their own data, so the gate only
   * opens once the codes have actually left the screen — a failed clipboard
   * write (insecure context, denied permission) must not count as saved.
   */
  const codesSaved = $derived(copied || downloaded || confirmedWrittenDown);

  function codesAsText(): string {
    return `${codes.join("\n")}\n`;
  }

  async function copyRecoveryCodes() {
    if (await copyToClipboard(codesAsText())) {
      copied = true;
      copyFailed = false;
    } else {
      copied = false;
      copyFailed = true;
    }
  }

  function downloadRecoveryCodes() {
    let url: string | undefined;
    try {
      const blob = new Blob([codesAsText()], { type: "text/plain" });
      url = URL.createObjectURL(blob);
      const link = document.createElement("a");
      link.href = url;
      link.download = "nocturne-recovery-codes.txt";
      link.click();
      downloaded = true;
    } catch {
      downloaded = false;
    } finally {
      if (url) URL.revokeObjectURL(url);
    }
  }
</script>

<div class="space-y-4">
  <div class="space-y-3">
    <div class="flex items-center gap-2">
      <ShieldCheck class="h-5 w-5 text-primary" />
      <h3 class="font-medium">Recovery Codes</h3>
    </div>
    <p class="text-sm text-muted-foreground">
      Save these recovery codes in a safe place. If you lose access to your
      passkey, you can use one of these codes to sign in. Each code can only be
      used once.
    </p>

    {#if codes.length > 0}
      <div class="grid grid-cols-2 gap-2 rounded-lg border bg-muted/50 p-4">
        {#each codes as code}
          <code
            class="rounded bg-background px-2 py-1 text-center text-sm font-mono"
          >
            {code}
          </code>
        {/each}
      </div>

      <div class="grid gap-2 @sm:grid-cols-2">
        <Button
          variant={copied ? "outline" : "default"}
          class="w-full"
          onclick={copyRecoveryCodes}
        >
          {#if copied}
            <Check class="mr-2 h-4 w-4" />
            Codes copied
          {:else}
            <Copy class="mr-2 h-4 w-4" />
            Copy recovery codes
          {/if}
        </Button>
        <Button variant="outline" class="w-full" onclick={downloadRecoveryCodes}>
          {#if downloaded}
            <Check class="mr-2 h-4 w-4" />
            Codes downloaded
          {:else}
            <Download class="mr-2 h-4 w-4" />
            Download codes
          {/if}
        </Button>
      </div>

      {#if copyFailed}
        <div
          class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
        >
          <TriangleAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
          <p class="text-sm text-destructive">
            The codes weren't copied — your browser blocked clipboard access.
            Download them, or write them down and confirm below.
          </p>
        </div>
      {/if}

      {#if !copied && !downloaded}
        <label class="flex items-start gap-2 text-sm">
          <Checkbox
            checked={confirmedWrittenDown}
            onCheckedChange={(checked: boolean) =>
              (confirmedWrittenDown = checked)}
          />
          <span>I've written down my recovery codes</span>
        </label>
      {/if}

      {#if !codesSaved}
        <p class="text-center text-xs text-muted-foreground">
          Save your recovery codes before continuing.
        </p>
      {/if}
    {:else}
      <p class="text-sm text-muted-foreground">
        No recovery codes were returned. You can generate new ones later from
        your account settings.
      </p>
    {/if}
  </div>

  <Button
    class="w-full"
    size="lg"
    disabled={codes.length > 0 && !codesSaved}
    onclick={onContinue}
  >
    {continueLabel}
  </Button>
</div>
