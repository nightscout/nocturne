<script lang="ts">
  import { resolve } from "$app/paths";

  interface Props {
    emailRecoveryOffered: boolean;
  }

  let { emailRecoveryOffered }: Props = $props();
</script>

<p class="text-sm text-muted-foreground" data-testid="recovery-code-next-step">
  {#if emailRecoveryOffered}
    <a href={resolve("auth/recovery/email")} class="underline hover:text-foreground">Request a new link</a>
    by email, or ask an administrator for a new code.
  {:else}
    Ask an administrator for a new code.
  {/if}
</p>
