<script lang="ts">
  import "../app.css";
  import { createRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { onMount } from "svelte";
  import {
    PUBLIC_WEBSOCKET_RECONNECT_ATTEMPTS,
    PUBLIC_WEBSOCKET_RECONNECT_DELAY,
    PUBLIC_WEBSOCKET_MAX_RECONNECT_DELAY,
    PUBLIC_WEBSOCKET_PING_TIMEOUT,
    PUBLIC_WEBSOCKET_PING_INTERVAL,
  } from "$env/static/public";

  const { children } = $props();

  // WebSocket bridge is integrated into the SvelteKit dev server
  const config = {
    url: typeof window !== "undefined" ? window.location.origin : "",
    reconnectAttempts: parseInt(PUBLIC_WEBSOCKET_RECONNECT_ATTEMPTS) || 10,
    reconnectDelay: parseInt(PUBLIC_WEBSOCKET_RECONNECT_DELAY) || 5000,
    maxReconnectDelay: parseInt(PUBLIC_WEBSOCKET_MAX_RECONNECT_DELAY) || 30000,
    pingTimeout: parseInt(PUBLIC_WEBSOCKET_PING_TIMEOUT) || 60000,
    pingInterval: parseInt(PUBLIC_WEBSOCKET_PING_INTERVAL) || 25000,
  };

  const realtimeStore = createRealtimeStore(config);

  onMount(async () => {
    await realtimeStore.initialize();
  });
</script>

<svelte:boundary>
  {@render children()}

  {#snippet pending()}
    Loading...
  {/snippet}
  {#snippet failed(e)}
    Error loading entries: {JSON.stringify(e)}
  {/snippet}
</svelte:boundary>
