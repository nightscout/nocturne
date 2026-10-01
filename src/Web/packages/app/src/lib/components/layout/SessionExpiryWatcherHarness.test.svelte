<script lang="ts">
  import {
    createAuthStore,
    type AuthStore,
  } from "$lib/stores/auth-store.svelte";
  import SessionExpiryWatcher from "./SessionExpiryWatcher.svelte";

  const {
    tickMs = 30_000,
    onstore,
  }: { tickMs?: number; onstore?: (store: AuthStore) => void } = $props();

  // The watcher reads the store from context, as it does under the
  // authenticated layout.
  const store = createAuthStore();

  $effect(() => {
    onstore?.(store);
  });
</script>

<SessionExpiryWatcher {tickMs} />
