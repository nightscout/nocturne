<script lang="ts">
  import Shield from "@lucide/svelte/icons/shield";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import { ScopeDeletionAccess } from "#lib/api/index.js";

  /** Unknown until the API answers: no claim is better than a wrong one. */
  let { access }: { access: ScopeDeletionAccess | null | undefined } = $props();
</script>

{#if access === ScopeDeletionAccess.Full || access === ScopeDeletionAccess.ReadWrite}
  <div
    class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
  >
    <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
    <p class="text-sm text-destructive">
      {#if access === ScopeDeletionAccess.Full}
        This app is requesting full access, including the ability to delete
        data.
      {:else}
        This app can change and delete the data in the categories above.
      {/if}
    </p>
  </div>
{:else if access === ScopeDeletionAccess.None}
  <div class="flex items-start gap-3 rounded-md bg-muted/50 p-3">
    <Shield class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
    <p class="text-sm text-muted-foreground">This app cannot delete your data.</p>
  </div>
{/if}
