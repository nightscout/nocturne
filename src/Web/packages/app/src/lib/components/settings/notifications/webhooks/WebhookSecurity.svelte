<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Copy, Check } from "lucide-svelte";

  interface Props {
    secret: string;
    hasSecret: boolean;
    disabled?: boolean;
  }

  let {
    secret,
    hasSecret,
    disabled = false,
  }: Props = $props();

  let copied = $state(false);

  async function copySecret() {
    if (!secret) return;
    await navigator.clipboard.writeText(secret);
    copied = true;
    setTimeout(() => {
      copied = false;
    }, 2000);
  }
</script>

<div class="space-y-2">
  <Label>Signature Secret</Label>
  {#if secret}
    <div class="flex gap-2 min-w-0">
      <code class="flex-1 min-w-0 px-3 py-2 rounded-md bg-muted text-sm font-mono truncate">
        {secret}
      </code>
      <Button
        variant="outline"
        size="icon"
        onclick={copySecret}
        {disabled}
      >
        {#if copied}
          <Check class="h-4 w-4 text-green-500" />
        {:else}
          <Copy class="h-4 w-4" />
        {/if}
      </Button>
    </div>
  {:else}
    <p class="px-3 py-2 rounded-md bg-muted text-sm text-muted-foreground">
      {hasSecret ? "Secret already configured" : "Secret will be generated on save"}
    </p>
  {/if}
  <p class="text-xs text-muted-foreground">
    The API signs each webhook with HMAC-SHA256 using this secret.
  </p>
</div>
