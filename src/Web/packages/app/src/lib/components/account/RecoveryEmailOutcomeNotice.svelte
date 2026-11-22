<script lang="ts">
  import type { RecoveryEmailOutcome } from "./recovery-email-outcome.js";

  interface Props {
    outcome: RecoveryEmailOutcome;
    /** The surface has a Resend button. Where it has none, the notice points to account settings. */
    canResend: boolean;
    class?: string;
  }

  let { outcome, canResend, class: className }: Props = $props();
</script>

{#if outcome === "sent"}
  <p class={className} role="status">We sent a link to your address. Follow the link to confirm it.</p>
{:else if outcome === "throttled"}
  <p class={className} role="status">
    {#if canResend}
      We saved your address. We sent a link a short time ago, so we did not send another. Wait a couple of minutes, then select Resend.
    {:else}
      We saved your address. We sent a link a short time ago, so we did not send another. In a couple of minutes, you can send a link from account settings.
    {/if}
  </p>
{:else if outcome === "failed"}
  <p class={className} role="status">
    {#if canResend}
      We saved your address, but we could not send the link. Try Resend later.
    {:else}
      We saved your address, but we could not send the link. Later, you can send a link from account settings.
    {/if}
  </p>
{/if}
