<script lang="ts">
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import SessionExpiryWarning from "./SessionExpiryWarning.svelte";

  interface Props {
    /** How often the countdown is recomputed, in milliseconds. */
    tickMs?: number;
  }

  const { tickMs = 30_000 }: Props = $props();

  const auth = getAuthStore();

  // The store's own `timeUntilExpiry` reads the wall clock inside a $derived,
  // so it only recomputes when the expiry itself changes. The countdown needs
  // a reactive clock of its own.
  let now = $state(Date.now());

  $effect(() => {
    const interval = setInterval(() => {
      now = Date.now();
    }, tickMs);
    return () => clearInterval(interval);
  });

  const expiresAt = $derived(auth.expiresAt?.getTime() ?? null);

  const timeUntilExpiry = $derived(
    expiresAt === null ? 0 : Math.max(0, Math.floor((expiresAt - now) / 1000))
  );

  // Keyed to the expiry it was raised against, so a dismissal does not silence
  // the warning for a session that has since been extended.
  let dismissedFor = $state<number | null>(null);

  const dismissed = $derived(
    dismissedFor !== null && dismissedFor === expiresAt
  );
</script>

{#if auth.isAuthenticated && !dismissed}
  <SessionExpiryWarning
    {timeUntilExpiry}
    onRefresh={() => auth.loadSession()}
    onDismiss={() => (dismissedFor = expiresAt)}
  />
{/if}
